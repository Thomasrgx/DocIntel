using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using DocIntel.Core.Authorization;
using DocIntel.Core.Models;
using DocIntel.Core.Repositories;
using DocIntel.Core.Repositories.Query;
using DocIntel.Core.Settings;
using DocIntel.Core.Utils.Features;
using DocIntel.Core.Utils.Indexation.SolR;
using DocIntel.Core.Utils.Observables;
using Microsoft.Extensions.Logging;
using SolrNet;
using Synsharp;

namespace DocIntel.Core.Utils;

public class DocumentAnalyzerUtility
{
    private readonly ApplicationSettings _appSettings;
    private readonly IDocumentRepository _documentRepository;
    private readonly ITagFacetRepository _facetRepository;
    private readonly ILogger<DocumentAnalyzerUtility> _logger;
    private readonly IObservablesUtility _observablesUtility;
    private readonly ISolrOperations<IndexedDocument> _solr;
    private readonly TagUtility _tagUtility;
    private readonly ISynapseRepository _observablesRepository;

    public DocumentAnalyzerUtility(ILogger<DocumentAnalyzerUtility> logger,
        IDocumentRepository documentRepository,
        ApplicationSettings appSettings, 
        ISolrOperations<IndexedDocument> solr,
        IObservablesUtility observablesUtility,
        TagUtility tagUtility,
        ISynapseRepository observablesRepository, 
        ITagFacetRepository facetRepository)
    {
        _logger = logger;
        _documentRepository = documentRepository;
        _appSettings = appSettings;
        _solr = solr;
        _observablesUtility = observablesUtility;
        _tagUtility = tagUtility;
        _observablesRepository = observablesRepository;
        _facetRepository = facetRepository;
    }
    public async Task Analyze(Guid documentId, AmbientContext ambientContext)
        {
            
            try
            {
                var document =
                    await _documentRepository.GetAsync(ambientContext, documentId, new[]
                    {
                        nameof(Document.Classification),
                        nameof(Document.ReleasableTo),
                        nameof(Document.EyesOnly),
                        nameof(Document.Files),
                        nameof(Document.DocumentTags),
                        nameof(Document.DocumentTags) + "." + nameof(DocumentTag.Tag),
                        nameof(Document.DocumentTags) + "." + nameof(DocumentTag.Tag) + "." + nameof(Tag.Facet)
                    });
                
                var tagCache = new HashSet<Tag>();
                var facetCache = new HashSet<TagFacet>();
                
                var doExtractObservables = true;
                if (document.MetaData != default && document.MetaData.ContainsKey("ExtractObservables"))
                    if (document.MetaData["ExtractObservables"]?.ToString()?.ToLower() == "false")
                        doExtractObservables = false;

                foreach (var file in document.Files.Where(_ =>
                    (_.MimeType == "application/pdf") | _.MimeType.StartsWith("text/")))
                {
                    var filename = Path.Combine(_appSettings.DocFolder, file.Filepath);
                    if (File.Exists(filename))
                    {
                        try
                        {
                            await using var f = File.OpenRead(filename);
                            var response = await _solr.ExtractAsync(new ExtractParameters(f, document.DocumentId.ToString())
                            {
                                ExtractOnly = true,
                                ExtractFormat = ExtractFormat.Text,
                                StreamType = file.MimeType
                            });

                            var metadata = response.Metadata.ToDictionary(_ => _.FieldName, _ => _.Value);
                            var title = ExtractTitle(metadata);

                            var date = ExtractDate(metadata);
                            if (date == DateTime.MinValue)
                                date = DateTime.UtcNow;

                            await DetectAutoExtractFacet(response, ambientContext, document, tagCache, facetCache);

                            if (doExtractObservables)
                            {
                                try
                                {
                                    var view = await _observablesRepository.CreateView(document);
                                    var fileObservables = await _observablesUtility.ExtractDataAsync(document, file, response.Content).ToListAsync();
                                    await _observablesUtility.AnnotateAsync(document, file, fileObservables);
                                    await _observablesRepository.Add(fileObservables, document, file, view);
                                }
                                catch (SynapseException e)
                                {
                                    _logger.LogError($"Could not extract observable in file '{file.FileId}' on document '{document.DocumentId}': {e.Message}");
                                }
                                catch (SynapseError e)
                                {
                                    _logger.LogError($"Could not extract observable in file '{file.FileId}' on document '{document.DocumentId}': {e.Message}");
                                }
                            }

                            if (string.IsNullOrEmpty(document.Title) & !string.IsNullOrEmpty(title))
                                document.Title = title;

                            if (file.DocumentDate == DateTime.MinValue)
                                file.DocumentDate = date;
                        }
                        catch (SolrNet.Exceptions.SolrConnectionException e)
                        {
                            _logger.LogError($"Document {document.DocumentId} could not be analyzed due to an error with SolR: {e.Url}");
                            _logger.LogError(e.ToString());
                        }
                    }
                }

                if (document.DocumentDate == DateTime.MinValue)
                {
                    var ddate = document.Files?.Min(_ => _.DocumentDate) ?? DateTime.MinValue;
                    if (ddate == DateTime.MinValue)
                        ddate = DateTime.UtcNow;
                    document.DocumentDate = ddate;
                }

                if (document.Status == DocumentStatus.Submitted)
                    document.Status = DocumentStatus.Analyzed;

                _logger.LogDebug($"Status of document '{document.DocumentId}' is '{document.Status}'.");
                ambientContext.DatabaseContext.Update(document);

                await ambientContext.DatabaseContext.SaveChangesAsync();
                
                // TODO Use structured logging
                _logger.LogInformation($"Document {document.Reference} ({document.DocumentId}) successfully analyzed.");
            }
            catch (Exception e)
            {
                _logger.LogError($"Document {documentId} could not be analyzed ({e.GetType()} {e.Message}).");
                _logger.LogError(e.StackTrace);
                _logger.LogError(e.InnerException?.Message);
            }
        }

        private static DateTime ExtractDate(Dictionary<string, string> metadata)
        {
            var date = DateTime.MinValue;
            var dateFields = new[]
                {"dcterms:created", "meta:creation-date", "created", "Creation-Date", "pdf:docinfo:created"};
            foreach (var df in dateFields)
                if (metadata.ContainsKey(df) && !string.IsNullOrEmpty(metadata[df]))
                    if (DateTime.TryParseExact(metadata[df], "yyyy-MM-ddThh:mm:ssZ", CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal, out date))
                        break;

            return date;
        }

        private static string ExtractTitle(Dictionary<string, string> metadata)
        {
            var title = "";
            var titleFields = new[] {"dc:title", "title"};
            foreach (var df in titleFields)
                if (metadata.ContainsKey(df) && !string.IsNullOrEmpty(metadata[df]))
                {
                    title = metadata[df];
                    break;
                }

            return title;
        }

        private async Task DetectAutoExtractFacet(ExtractResponse response, AmbientContext ambientContext,
            Document document, HashSet<Tag> tagCache, HashSet<TagFacet> facetCache)
        {
            var facets = await _facetRepository.GetAllAsync(ambientContext, new FacetQuery(), new string[] { "Tags" }).ToListAsync();

            foreach (var facet in facets)
            {
                var extractor = new TagFacetFeatureExtractor(facet);
                var patternMatches = extractor.Extract(response.Content);

                if (patternMatches.Any())
                    _logger.LogDebug($"Detected tags in '{facet.Title}': " + string.Join(",", patternMatches));

                var tags = _tagUtility
                    .GetOrCreateTags(ambientContext, patternMatches.Where(match => !string.IsNullOrEmpty(match)), tagCache, facetCache);
                
                foreach (var tag in tags)
                {   
                    if (!document.DocumentTags.Any(_ =>
                            _.DocumentId == document.DocumentId && _.TagId == tag.TagId))
                        document.DocumentTags.Add(new DocumentTag
                        {
                            DocumentId = document.DocumentId,
                            TagId = tag.TagId
                        });
                }
            }
        }
}
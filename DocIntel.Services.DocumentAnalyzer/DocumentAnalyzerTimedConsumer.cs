using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DocIntel.Core.Authorization;
using DocIntel.Core.Models;
using DocIntel.Core.Repositories;
using DocIntel.Core.Services;
using DocIntel.Core.Settings;
using DocIntel.Core.Utils;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DocIntel.Services.DocumentAnalyzer;

public class DocumentAnalyzerTimedConsumer : DynamicContextConsumer, IHostedService, IDisposable
{
    private int executionCount = 0;
    private readonly ILogger<DocumentAnalyzerTimedConsumer> _logger;
    private Timer? _timer = null;
    private IDocumentRepository _documentRepository;
    private DocumentAnalyzerUtility _documentAnalyzerUtility;

    public DocumentAnalyzerTimedConsumer(ILogger<DocumentAnalyzerTimedConsumer> logger,
        IDocumentRepository documentRepository,
        DocumentAnalyzerUtility documentAnalyzerUtility,
        ApplicationSettings appSettings,
        IServiceProvider serviceProvider,
        AppUserClaimsPrincipalFactory userClaimsPrincipalFactory)
        : base(appSettings, serviceProvider, userClaimsPrincipalFactory)
    {
        _logger = logger;
        _documentRepository = documentRepository;
        _documentAnalyzerUtility = documentAnalyzerUtility;
    }

    public async Task StartAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Timed Hosted Service running.");
        await DoWork(null);
        
        var fromMinutes = TimeSpan.FromMinutes(_appSettings.Schedule.AnalyzerFrequencyCheck);
        _timer = new Timer(async _ => await DoWork(_), null, fromMinutes, fromMinutes);
    }

    private async Task DoWork(object? state)
    {
        var count = Interlocked.Increment(ref executionCount);
        _logger.LogInformation(
            "Timed Hosted Service is working. Count: {Count}", count);
        
        var ambientContext = await GetAmbientContext();
            
        while (await _documentRepository.GetAllAsync(ambientContext,
                   _ => _.Where(__ => __.Status == DocumentStatus.Submitted)).CountAsync() > 0)
        {
            var submitted = await _documentRepository.GetAllAsync(ambientContext,
                _ => _.Where(__ => __.Status == DocumentStatus.Submitted))
                .OrderByDescending(__ => __.RegistrationDate).FirstAsync();
            await _documentAnalyzerUtility.Analyze(submitted.DocumentId, ambientContext);
        }
    }

    public Task StopAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Timed Hosted Service is stopping.");

        _timer?.Change(Timeout.Infinite, 0);

        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _timer?.Dispose();
    }
}
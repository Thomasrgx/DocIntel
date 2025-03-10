/* DocIntel
 * Copyright (C) 2018-2021 Belgian Defense, Antoine Cailliau
 * 
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 * 
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
 * GNU Affero General Public License for more details.
 * 
 * You should have received a copy of the GNU Affero General Public License
 * along with this program.  If not, see <http://www.gnu.org/licenses/>.
*/

using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

using DocIntel.Core.Exceptions;
using DocIntel.Core.Importers;
using DocIntel.Core.Models;
using DocIntel.Core.Repositories;
using DocIntel.Core.Settings;

using MailKit;
using MailKit.Net.Imap;
using MailKit.Security;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

using MimeKit;

namespace DocIntel.Core.Scrapers
{
    [Scraper("6fae6c6b-f343-4317-a234-3d78415017cb",
        Name = "Mailbox",
        Description = "Scraper for mailboxes",
        Patterns = new[]
        {
            @"imap://[A-Za-z@\.-_]+/[a-zA-Z\.-_]+/;uid=.+"
        })]
    public class MailboxScraper : DefaultScraper
    {
        private readonly ILogger<MailboxScraper> _logger;
        private readonly Scraper _scraper;
        private readonly ApplicationSettings _settings;

        public MailboxScraper(Scraper scraper, IServiceProvider serviceProvider) : base(serviceProvider)
        {
            _scraper = scraper;
            _logger = (ILogger<MailboxScraper>) serviceProvider.GetService(typeof(ILogger<MailboxScraper>));
            _settings = (ApplicationSettings) serviceProvider.GetService(typeof(ApplicationSettings));
        }

        [ScraperSetting("Host")] public string Host { get; set; }

        [ScraperSetting("Port", DefaultValue = "0")]
        public int Port { get; set; }

        [ScraperSetting("SSL", DefaultValue = "true")]
        public bool SSL { get; set; }

        [ScraperSetting("Email")] public string Email { get; set; }

        [ScraperSetting("Username")] public string Username { get; set; }

        [ScraperSetting("Password", Type = AttributeFieldType.Password)]
        public string Password { get; set; }

        [ScraperSetting("Mark as read", DefaultValue = "false")]
        public bool MarkAsRead { get; set; }

        [ScraperSetting("Move when processed", DefaultValue = "false")]
        public bool MoveWhenProcessed { get; set; }

        [ScraperSetting("Move To", DefaultValue = "")]
        public string MoveFolder { get; set; }

        public override async Task<bool> Scrape(SubmittedDocument message)
        {
            Init();
            var context = GetContext();
            var match = Regex.Match(message.URL, @"imap://([A-Za-z@\.-_]+)/([a-zA-Z\.-_]+)/;uid=(.+)");
            var email = match.Groups[1].ToString();
            var inbox = match.Groups[2].ToString();
            var uid = match.Groups[3].ToString();

            if (email != Email)
                return true;

            await ImportEmail(context, uid, message);
            _logger.LogDebug("save");

            _documentRepository.DeleteSubmittedDocument(context, message.SubmittedDocumentId);
            await context.DatabaseContext.SaveChangesAsync();
            return false;
        }

        private async Task ImportEmail(AmbientContext context, string uid, SubmittedDocument submittedDocument)
        {
            if (!string.IsNullOrEmpty(Host) && !string.IsNullOrEmpty(Username))
                using (var client = new ImapClient())
                {
                    if (SSL)
                        await client.ConnectAsync(Host, Port, SecureSocketOptions.SslOnConnect);
                    else
                        await client.ConnectAsync(Host, Port);

                    await client.AuthenticateAsync(Username, Password);
                    _logger.LogDebug("Authenticated");

                    await client.Inbox.OpenAsync(FolderAccess.ReadWrite);
                    _logger.LogDebug("INBOX opened");

                    var uniqueIds = new[] {UniqueId.Parse(uid)};
                    var items = await client.Inbox.FetchAsync(uniqueIds,
                        MessageSummaryItems.BodyStructure | MessageSummaryItems.All | MessageSummaryItems.UniqueId);

                    foreach (var item in items)
                    {
                        Document document;
                        document = new Document
                        {
                            Title = item.NormalizedSubject,
                            ShortDescription = "",
                            Status = DocumentStatus.Submitted,
                            DocumentDate = item.Date.UtcDateTime
                        };
                        document = await AddAsync(_scraper, context, document, submittedDocument);

                        var allFilesKnown = true;
                        foreach (var m in item.BodyParts.Where(x =>
                            x.IsAttachment && x.ContentType.MimeType == "application/pdf"))
                        {
                            _logger.LogDebug("Got the attachement ");
                            var entity = client.Inbox.GetBodyPart(item.UniqueId, m);
                            var part = (MimePart) entity;

                            var documentFile = new DocumentFile
                            {
                                Document = document,
                                MimeType = m.ContentType.MimeType,
                                Filename = m.FileName,
                                Title = m.FileName,
                                ClassificationId = _classificationRepository.GetDefault(context)?.ClassificationId,
                                DocumentDate = item.Date.UtcDateTime,
                                Visible = true,
                                Preview = true
                            };

                            await using var stream = new MemoryStream();
                            if (part.Content.Encoding != default)
                                await part.Content.DecodeToAsync(stream);
                            else
                                await part.Content.Stream.CopyToAsync(stream);

                            stream.Seek(0, SeekOrigin.Begin);

                            try
                            {
                                documentFile = await AddAsync(_scraper, context, documentFile, stream);
                                allFilesKnown = false;
                            }
                            catch (FileAlreadyKnownException)
                            {
                                _logger.LogDebug("File is already known.");
                            }
                        }

                        // Do not import documents for which all files are already known.
                        if (allFilesKnown) context.DatabaseContext.Entry(document).State = EntityState.Detached;
                    }

                    if (MarkAsRead) await client.Inbox.AddFlagsAsync(uniqueIds, MessageFlags.Seen, true);

                    if (MoveWhenProcessed)
                    {
                        foreach (var folder in client.Inbox.GetSubfolders())
                            _logger.LogDebug("[folder] {0}", folder.FullName);

                        var dest = await client.Inbox.GetSubfolderAsync(MoveFolder);
                        await client.Inbox.MoveToAsync(uniqueIds, dest);
                    }

                    await client.DisconnectAsync(true);
                }
            else
                _logger.LogDebug("Invalid AccessKey or SecretKey");
        }
    }
}
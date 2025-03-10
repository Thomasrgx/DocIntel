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
using System.Threading;
using System.Threading.Tasks;

using DocIntel.Core.Services;

using Microsoft.Extensions.DependencyInjection;

namespace DocIntel.Services.Newsletters
{
    class EmailNotificationHostedService : DocIntelHostedService
    {
        protected override string WorkerName => "Email Notification Sender";

        protected override Task Init()
        {
            return Task.CompletedTask;
        }

        protected override async Task Run(CancellationToken cancellationToken)
        {
            var worker = _serviceProvider.GetService<NewsletterSender>();
            if (worker != null)
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    await worker.RunAsync();
                    // TODO Support weekly and hourly newsletters, for example for saved searches.
                    await Task.Delay(TimeSpan.FromHours(24), cancellationToken);
                }
            }
        }

        public EmailNotificationHostedService(IServiceProvider serviceProvider) : base(serviceProvider)
        {
        }
    }
}
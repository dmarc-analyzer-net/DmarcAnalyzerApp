using DmarcAnalyzer.Api.Application.Common;
using DmarcAnalyzer.Api.Contracts.MailboxSources;

namespace DmarcAnalyzer.Api.Application.MailboxSources;

public interface IMailboxSourceService
{
    Task<IReadOnlyList<MailboxSourceDto>> ListAsync(CancellationToken ct);
    Task<ServiceResult<MailboxSourceDto>> CreateAsync(CreateMailboxSourceRequest request, CancellationToken ct);
    Task<ServiceResult<MailboxSourceDto>> UpdateAsync(Guid id, UpdateMailboxSourceRequest request, CancellationToken ct);
}

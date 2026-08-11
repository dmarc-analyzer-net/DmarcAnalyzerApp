using DmarcAnalyzer.Api.Application.Common;
using DmarcAnalyzer.Api.Contracts.ReportSources;

namespace DmarcAnalyzer.Api.Application.ReportSources;

public interface IReportSourceService
{
    Task<IReadOnlyList<ReportSourceDto>> ListAsync(CancellationToken ct);
    Task<ServiceResult<ReportSourceDto>> CreateAsync(CreateReportSourceRequest request, CancellationToken ct);
    Task<ServiceResult<ReportSourceDto>> UpdateAsync(Guid id, UpdateReportSourceRequest request, CancellationToken ct);
}

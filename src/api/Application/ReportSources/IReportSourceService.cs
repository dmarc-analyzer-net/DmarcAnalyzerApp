using DmarcAnalyzer.Api.Application.Common;
using DmarcAnalyzer.Api.Contracts.ReportSources;

namespace DmarcAnalyzer.Api.Application.ReportSources;

/// <summary>Report-source CRUD; secrets are encrypted at rest and never read back out.</summary>
public interface IReportSourceService
{
    /// <summary>Every source, by name.</summary>
    Task<IReadOnlyList<ReportSourceDto>> ListAsync(CancellationToken ct);

    /// <summary>Creates a source after per-protocol validation of the fields that protocol needs.</summary>
    Task<ServiceResult<ReportSourceDto>> CreateAsync(CreateReportSourceRequest request, CancellationToken ct);

    /// <summary>Partial update; an omitted password/secret keeps the stored one.</summary>
    Task<ServiceResult<ReportSourceDto>> UpdateAsync(Guid id, UpdateReportSourceRequest request, CancellationToken ct);
}

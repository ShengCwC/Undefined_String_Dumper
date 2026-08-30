using Undefined.StringDumper.Core.Models;

namespace Undefined.StringDumper.Core.Services;

public interface IProcessCatalog
{
    Task<IReadOnlyList<JavaProcessInfo>> GetJavaProcessesAsync(CancellationToken cancellationToken = default);
}

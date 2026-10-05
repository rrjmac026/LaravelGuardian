using LaravelGuardian.Core.Models;

namespace LaravelGuardian.Core.Interfaces;

public interface ISeederScanner
{
    Task<IReadOnlyList<SeededAccount>> ScanAsync(string projectPath, CancellationToken ct = default);
}

public interface ISecretStore
{
    void Save(string projectPath, string email, string password);
    AuthProfile? Load(string projectPath);
    void Delete(string projectPath);
}

public interface IAuthLogin
{
    Task<AuthSession> LoginAsync(string baseUrl, string email, string password, CancellationToken ct = default);
}
using LaravelGuardian.Core.Models;

namespace LaravelGuardian.Core.Interfaces;

public interface ISeederScanner
{
    Task<IReadOnlyList<SeededAccount>> ScanAsync(string projectPath, CancellationToken ct = default);
}

public interface ISecretStore
{
    /// Adds the account, or replaces the saved one with the same email.
    void Save(string projectPath, string email, string? role, string password);

    /// Every account remembered for this project (empty list when none).
    IReadOnlyList<AuthProfile> LoadAll(string projectPath);

    /// Removes one remembered account.
    void Delete(string projectPath, string email);

    /// Removes every remembered account of the project.
    void Delete(string projectPath);
}

public interface IAuthLogin
{
    Task<AuthSession> LoginAsync(string baseUrl, string email, string password, CancellationToken ct = default);
}
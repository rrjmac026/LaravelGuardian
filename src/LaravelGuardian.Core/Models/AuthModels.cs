using System.Net;

namespace LaravelGuardian.Core.Models;

/// An account found by reading the project's seeders. Read-only discovery, nothing is written.
public record SeededAccount(string Email, string? Role, string? Password, string Source)
{
    public string Display => string.IsNullOrWhiteSpace(Role) ? Email : $"{Email} ({Role})";
}

/// A test account remembered for one project folder. A project can have several (one per role).
public class AuthProfile
{
    public string ProjectPath { get; set; } = "";
    public string Email { get; set; } = "";
    public string? Role { get; set; }
    public string? Password { get; set; }
}

/// Outcome of one login request, plus the cookies the authenticated checks reuse.
public class AuthSession
{
    public bool Success { get; set; }
    public string Email { get; set; } = "";

    /// Label of the role this session belongs to (admin, counselor, ...). Set by the caller.
    public string Role { get; set; } = "";

    public string Message { get; set; } = "";
    public CookieContainer? Cookies { get; set; }
    public TestResult Result { get; set; } = new();
}
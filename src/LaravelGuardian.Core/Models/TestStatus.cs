// Models/TestStatus.cs
namespace LaravelGuardian.Core.Models;

public enum TestStatus { Pass, Fail, Warning, Skipped, Blocked }
public enum Severity { Info, Low, Medium, High, Critical }
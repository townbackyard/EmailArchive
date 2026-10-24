namespace EmailArchive.Core.Auth;

/// <summary>Thrown when the Microsoft sign-in must be redone interactively. Maps to exit code 3.</summary>
public sealed class AuthRequiredException(string message, Exception? inner = null) : Exception(message, inner);

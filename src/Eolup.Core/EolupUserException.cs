namespace Eolup.Core;

/// <summary>
/// An expected, explainable failure (missing project file, project doesn't build
/// in its current state, etc.) — the CLI catches this and prints a clean message
/// instead of a raw stack trace. Anything else is a genuine bug and should still
/// surface as a crash.
/// </summary>
public class EolupUserException : Exception
{
    public EolupUserException(string message) : base(message)
    {
    }

    /// <param name="innerException">What actually went wrong, kept for whoever wants the detail.</param>
    public EolupUserException(string message, Exception? innerException) : base(message, innerException)
    {
    }
}

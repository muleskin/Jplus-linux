using Microsoft.Extensions.Logging;

namespace Jplus;

/// <summary>
/// JARVIS Work Mode — routing of spoken input while a project is attached
/// (work_mode.py).
///
/// The session machinery moved to RunExecutor (every message is now a recorded
/// run). What is left here is the cheap classifier that decides whether a
/// sentence is small talk (answer with Haiku) or work (spawn a run).
/// </summary>
public static partial class WorkMode
{
    public static readonly ILogger Log = Py.Log("jarvis.work_mode");

    public static readonly string[] CasualPatterns =
    [
        "what time", "what's the time", "what day",
        "what's the weather", "weather",
        "how are you", "are you there", "hey jarvis",
        "good morning", "good evening", "good night",
        "thank you", "thanks", "never mind", "nevermind",
        "stop", "cancel", "quit work mode", "exit work mode",
        "go back to chat", "regular mode",
        "how's it going", "what's up",
        "are you still there", "you there", "jarvis",
        "are you doing it", "is it working", "what happened",
        "did you hear me", "hello", "hey",
        "how's that coming", "hows that coming",
        "any update", "status update",
    ];

    private static readonly string[] ShortAcks = ["ok", "okay", "sure", "yes", "no", "yeah", "nah", "cool"];

    /// <summary>
    /// Detect if a message is casual chat vs work-related.
    /// Casual questions go to Haiku (fast). Work goes to claude -p (powerful).
    /// </summary>
    public static bool IsCasualQuestion(string text)
    {
        var t = (text ?? "").ToLowerInvariant().Trim();

        // Short greetings/acknowledgments (substring match, as the original).
        var words = t.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length <= 3 && ShortAcks.Any(w => t.Contains(w)))
            return true;

        return CasualPatterns.Any(p => t.Contains(p));
    }
}

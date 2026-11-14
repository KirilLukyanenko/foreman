namespace Foreman.Core;

/// <summary>
/// Token counts without a model call. Claude's tokenizer is not public, so this is an estimate: about 3.5 characters
/// per token for code and English, which errs on the high side for prose. Used only for budgets, never billed.
/// </summary>
public static class Tokens
{
    public const double CharsPerToken = 3.5;

    public static int Estimate(string text) => (int)Math.Ceiling(text.Length / CharsPerToken);
}

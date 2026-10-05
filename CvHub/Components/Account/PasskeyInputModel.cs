namespace CvHub.Components.Account;

public class PasskeyInputModel
{
    public string? CredentialJson { get; set; }
    public string? Error { get; set; }

    /// <summary>
    /// Set by the passkey custom element on every passkey submission, including one where the
    /// browser produced neither a credential nor an error. Without it, such a request looks
    /// exactly like an empty password form and the server answers with "Email and password is
    /// required" — which is meaningless during a passkey sign-in and reads as a failure the
    /// user cannot clear.
    /// </summary>
    public bool Attempted { get; set; }
}

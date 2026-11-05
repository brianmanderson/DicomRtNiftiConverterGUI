namespace DicomRtNifti.Web.Audit;

/// <summary>
/// Who is using the app: the user id that jobs and audit records carry, and whether the user may
/// see the audit log. A user id is not an identifier in the PHI sense (plan section 6: logs record
/// job ids, user ids, root ids and counts), so it may appear in the application log.
/// </summary>
public interface ICurrentUser
{
    string Id { get; }

    /// <summary>The Administrator role (plan section 6): the audit log, key downloads.</summary>
    bool IsAdministrator { get; }
}

/// <summary>
/// The first deployment target (D-33): loopback, one user, whose identity is the OS account the
/// server runs as, and who administers it. Brief W4-P replaces this registration with users and
/// roles from the configured authentication scheme before anything listens beyond loopback.
/// </summary>
public sealed class LocalUser : ICurrentUser
{
    public string Id { get; } = "local:" + Environment.UserName;

    public bool IsAdministrator => true;
}

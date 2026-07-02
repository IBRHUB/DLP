using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;

internal enum CryptAccessMode
{
    Read,
    Modify
}

internal sealed record CryptStatus(
    bool IsSupported,
    bool IsManaged,
    bool IsLocked,
    bool IsUnlocked,
    bool HasActiveOperations,
    string State,
    string Message,
    string Directory,
    DateTimeOffset? UnlockedUntilUtc);

internal sealed class CryptAccessScope : IDisposable
{
    private readonly string? _operationId;
    private bool _disposed;

    internal CryptAccessScope(string directoryPath, string? operationId)
    {
        DirectoryPath = directoryPath;
        _operationId = operationId;
    }

    public string DirectoryPath { get; }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Crypt.EndOperation(_operationId);
    }
}

internal static class Crypt
{
    private const string MutexName = @"Local\DLP_CryptAcl";
    private const int DefaultUnlockMinutes = 10;
    private static readonly TimeSpan StaleOperationAge = TimeSpan.FromHours(6);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public static TimeSpan DefaultUnlockDuration => TimeSpan.FromMinutes(DefaultUnlockMinutes);

    public static CryptStatus GetStatus()
    {
        try
        {
            return WithAclLock(() =>
            {
                string directoryPath = EnsureManagedDirectory();
                AccessState state = ReadState();
                bool hasActiveOperations = HasActiveOperations();

                if (!IsUserUnlockActive(state) && !hasActiveOperations)
                {
                    ClearState();
                    ApplyLockedAcl(directoryPath);
                    SetLockedAttributes(directoryPath);
                }
                else if (IsUserUnlockActive(state))
                {
                    ApplyUnlockedAcl(directoryPath, CryptAccessMode.Modify);
                    SetUnlockedAttributes(directoryPath);
                }

                return ReadStatus(directoryPath, ReadState(), HasActiveOperations());
            });
        }
        catch (Exception ex)
        {
            Program.Log($"Crypt download folder status unavailable: {ex.Message}");
            return UnsupportedStatus(ex.Message);
        }
    }

    public static CryptStatus UnlockForCurrentUser(TimeSpan duration)
    {
        try
        {
            return WithAclLock(() =>
            {
                string directoryPath = EnsureManagedDirectory();
                DateTimeOffset unlockedUntilUtc = DateTimeOffset.UtcNow.Add(duration);

                WriteState(new AccessState(unlockedUntilUtc));
                ApplyUnlockedAcl(directoryPath, CryptAccessMode.Modify);
                SetUnlockedAttributes(directoryPath);
                Program.Log($"DLP download folder unlocked until {unlockedUntilUtc:O}: {directoryPath}");

                return ReadStatus(directoryPath, ReadState(), HasActiveOperations());
            });
        }
        catch (Exception ex)
        {
            Program.Log($"Crypt download folder unlock failed: {ex}");
            return UnsupportedStatus("DLP could not unlock the download folder");
        }
    }

    public static CryptStatus LockForCurrentUser()
    {
        try
        {
            return WithAclLock(() =>
            {
                string directoryPath = EnsureManagedDirectory();

                if (HasActiveOperations())
                {
                    return ReadStatus(
                        directoryPath,
                        ReadState(),
                        hasActiveOperations: true,
                        messageOverride: "A download is still using the folder");
                }

                ClearState();
                ApplyLockedAcl(directoryPath);
                SetLockedAttributes(directoryPath);
                Program.Log($"DLP download folder locked: {directoryPath}");

                return ReadStatus(directoryPath, ReadState(), hasActiveOperations: false);
            });
        }
        catch (Exception ex)
        {
            Program.Log($"Crypt download folder lock failed: {ex}");
            return UnsupportedStatus("DLP could not lock the download folder");
        }
    }

    public static CryptAccessScope BeginOperationAccess(
        string reason,
        CryptAccessMode mode)
    {
        try
        {
            return WithAclLock(() =>
            {
                string directoryPath = EnsureManagedDirectory();
                string operationId = CreateOperation(reason, mode);

                try
                {
                    ApplyUnlockedAcl(directoryPath, mode);
                    SetUnlockedAttributes(directoryPath);
                    return new CryptAccessScope(directoryPath, operationId);
                }
                catch
                {
                    DeleteOperation(operationId);
                    throw;
                }
            });
        }
        catch (Exception ex)
        {
            Program.Log($"Crypt download folder operation access failed: {ex}");
            string directoryPath = Program.GetDownloadDirectory();

            try
            {
                Directory.CreateDirectory(directoryPath);
            }
            catch (Exception createEx)
            {
                Program.Log($"Crypt download folder fallback directory check failed: {createEx.Message}");
            }

            return new CryptAccessScope(directoryPath, operationId: null);
        }
    }

    internal static void EndOperation(string? operationId)
    {
        if (string.IsNullOrWhiteSpace(operationId))
        {
            return;
        }

        try
        {
            WithAclLock(() =>
            {
                string directoryPath = EnsureManagedDirectory();
                DeleteOperation(operationId);
                AccessState state = ReadState();

                if (HasActiveOperations())
                {
                    return true;
                }

                if (IsUserUnlockActive(state))
                {
                    ApplyUnlockedAcl(directoryPath, CryptAccessMode.Modify);
                    SetUnlockedAttributes(directoryPath);
                    return true;
                }

                ClearState();
                ApplyLockedAcl(directoryPath);
                SetLockedAttributes(directoryPath);
                return true;
            });
        }
        catch (Exception ex)
        {
            Program.Log($"Crypt download folder operation cleanup failed: {ex.Message}");
        }
    }

    private static CryptStatus ReadStatus(
        string directoryPath,
        AccessState state,
        bool hasActiveOperations,
        string? messageOverride = null)
    {
        DirectorySecurity security = new DirectoryInfo(directoryPath)
            .GetAccessControl(AccessControlSections.Access);
        SecurityIdentifier currentUser = GetCurrentUserSid();
        bool hasUserDataAccess = HasUserDataAccess(security, currentUser);
        bool isUserUnlockActive = IsUserUnlockActive(state);
        bool isManaged = security.AreAccessRulesProtected;
        bool isUnlocked = hasUserDataAccess || hasActiveOperations || isUserUnlockActive;
        string stateName = isUnlocked ? "unlocked" : "locked";
        string message = messageOverride
            ?? (isUnlocked
                ? BuildUnlockedMessage(state, hasActiveOperations)
                : "Folder is locked");

        return new CryptStatus(
            IsSupported: true,
            IsManaged: isManaged,
            IsLocked: !isUnlocked,
            IsUnlocked: isUnlocked,
            HasActiveOperations: hasActiveOperations,
            State: stateName,
            Message: message,
            Directory: directoryPath,
            UnlockedUntilUtc: state.UnlockedUntilUtc);
    }

    private static string BuildUnlockedMessage(AccessState state, bool hasActiveOperations)
    {
        if (hasActiveOperations)
        {
            return "Folder is unlocked for an active DLP operation";
        }

        if (state.UnlockedUntilUtc is { } untilUtc)
        {
            return $"Folder is unlocked until {untilUtc.LocalDateTime:t}";
        }

        return "Folder is unlocked";
    }

    private static CryptStatus UnsupportedStatus(string message)
    {
        return new CryptStatus(
            IsSupported: false,
            IsManaged: false,
            IsLocked: false,
            IsUnlocked: true,
            HasActiveOperations: false,
            State: "unsupported",
            Message: message,
            Directory: Program.GetDownloadDirectory(),
            UnlockedUntilUtc: null);
    }

    private static string EnsureManagedDirectory()
    {
        string directoryPath = Path.GetFullPath(Program.GetDownloadDirectory());
        string downloadsPath = Path.GetFullPath(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Downloads"));
        string expectedPath = Path.GetFullPath(Path.Combine(downloadsPath, "DLP"));

        if (!string.Equals(directoryPath, expectedPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("DLP refused to manage an unexpected download folder path");
        }

        if (!ManagedDirectoryExists(downloadsPath))
        {
            Directory.CreateDirectory(directoryPath);
        }

        Directory.CreateDirectory(StateDirectory);
        Directory.CreateDirectory(OperationDirectory);
        CleanupStaleOperations();

        return directoryPath;
    }

    private static bool ManagedDirectoryExists(string downloadsPath)
    {
        try
        {
            return Directory.EnumerateFileSystemEntries(downloadsPath, "DLP", SearchOption.TopDirectoryOnly).Any();
        }
        catch (Exception ex)
        {
            Program.Log($"Crypt parent folder check failed: {downloadsPath}: {ex.Message}");
            return Directory.Exists(Program.GetDownloadDirectory());
        }
    }

    private static void ApplyLockedAcl(string directoryPath)
    {
        DirectoryInfo directory = new(directoryPath);
        ApplyUnlockedRootAcl(directory, CryptAccessMode.Modify, includeAdministrators: false);
        ApplyAclToChildren(directory, locked: true, CryptAccessMode.Read);
        ApplyLockedRootAcl(directory);
    }

    private static void ApplyLockedRootAcl(DirectoryInfo directory)
    {
        DirectorySecurity security = directory.GetAccessControl(AccessControlSections.Access);
        SecurityIdentifier currentUser = GetCurrentUserSid();

        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        RemoveAllAccessRules(security);
        AddInheritedRule(security, WellKnownSidType.LocalSystemSid, FileSystemRights.FullControl);
        AddInheritedRule(
            security,
            currentUser,
            GetLockedUserRights());

        directory.SetAccessControl(security);
    }

    private static void ApplyUnlockedAcl(string directoryPath, CryptAccessMode mode)
    {
        DirectoryInfo directory = new(directoryPath);
        ApplyUnlockedRootAcl(directory, mode, includeAdministrators: true);
        ApplyAclToChildren(directory, locked: false, mode);
    }

    private static void ApplyUnlockedRootAcl(
        DirectoryInfo directory,
        CryptAccessMode mode,
        bool includeAdministrators)
    {
        DirectorySecurity security = directory.GetAccessControl(AccessControlSections.Access);
        FileSystemRights userRights = GetUserRights(mode);

        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        RemoveAllAccessRules(security);
        AddInheritedRule(security, WellKnownSidType.LocalSystemSid, FileSystemRights.FullControl);

        if (includeAdministrators)
        {
            AddInheritedRule(security, WellKnownSidType.BuiltinAdministratorsSid, FileSystemRights.FullControl);
        }

        AddInheritedRule(security, GetCurrentUserSid(), userRights);

        directory.SetAccessControl(security);
    }

    private static void ApplyAclToChildren(DirectoryInfo root, bool locked, CryptAccessMode mode)
    {
        foreach (FileSystemInfo child in EnumerateChildren(root))
        {
            try
            {
                if (IsReparsePoint(child))
                {
                    continue;
                }

                if (child is DirectoryInfo childDirectory)
                {
                    if (locked)
                    {
                        ApplyLockedDirectoryAcl(childDirectory);
                    }
                    else
                    {
                        ApplyUnlockedDirectoryAcl(childDirectory, mode);
                    }

                    ApplyAclToChildren(childDirectory, locked, mode);
                    continue;
                }

                if (child is FileInfo childFile)
                {
                    if (locked)
                    {
                        ApplyLockedFileAcl(childFile);
                    }
                    else
                    {
                        ApplyUnlockedFileAcl(childFile, mode);
                    }
                }
            }
            catch (Exception ex)
            {
                Program.Log($"Crypt child ACL update skipped: {child.FullName}: {ex.Message}");
            }
        }
    }

    private static IEnumerable<FileSystemInfo> EnumerateChildren(DirectoryInfo directory)
    {
        try
        {
            return directory.EnumerateFileSystemInfos("*", SearchOption.TopDirectoryOnly).ToArray();
        }
        catch (Exception ex)
        {
            Program.Log($"Crypt child enumeration failed: {directory.FullName}: {ex.Message}");
            return Array.Empty<FileSystemInfo>();
        }
    }

    private static void ApplyLockedDirectoryAcl(DirectoryInfo directory)
    {
        DirectorySecurity security = directory.GetAccessControl(AccessControlSections.Access);
        SecurityIdentifier currentUser = GetCurrentUserSid();

        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        RemoveAllAccessRules(security);
        AddInheritedRule(security, WellKnownSidType.LocalSystemSid, FileSystemRights.FullControl);
        AddInheritedRule(
            security,
            currentUser,
            GetLockedUserRights());

        directory.SetAccessControl(security);
    }

    private static void ApplyUnlockedDirectoryAcl(DirectoryInfo directory, CryptAccessMode mode)
    {
        DirectorySecurity security = directory.GetAccessControl(AccessControlSections.Access);
        FileSystemRights userRights = GetUserRights(mode);

        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        RemoveAllAccessRules(security);
        AddInheritedRule(security, WellKnownSidType.LocalSystemSid, FileSystemRights.FullControl);
        AddInheritedRule(security, WellKnownSidType.BuiltinAdministratorsSid, FileSystemRights.FullControl);
        AddInheritedRule(security, GetCurrentUserSid(), userRights);

        directory.SetAccessControl(security);
    }

    private static void ApplyLockedFileAcl(FileInfo file)
    {
        FileSecurity security = file.GetAccessControl(AccessControlSections.Access);
        SecurityIdentifier currentUser = GetCurrentUserSid();

        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        RemoveAllAccessRules(security);
        AddFileRule(security, WellKnownSidType.LocalSystemSid, FileSystemRights.FullControl);
        AddFileRule(
            security,
            currentUser,
            GetLockedUserRights());

        file.SetAccessControl(security);
    }

    private static void ApplyUnlockedFileAcl(FileInfo file, CryptAccessMode mode)
    {
        FileSecurity security = file.GetAccessControl(AccessControlSections.Access);

        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        RemoveAllAccessRules(security);
        AddFileRule(security, WellKnownSidType.LocalSystemSid, FileSystemRights.FullControl);
        AddFileRule(security, WellKnownSidType.BuiltinAdministratorsSid, FileSystemRights.FullControl);
        AddFileRule(security, GetCurrentUserSid(), GetUserRights(mode));

        file.SetAccessControl(security);
    }

    private static FileSystemRights GetUserRights(CryptAccessMode mode)
    {
        return mode == CryptAccessMode.Modify
            ? FileSystemRights.Modify | FileSystemRights.Synchronize
            : FileSystemRights.ReadAndExecute | FileSystemRights.ListDirectory | FileSystemRights.Synchronize;
    }

    private static FileSystemRights GetLockedUserRights()
    {
        return FileSystemRights.ReadPermissions
            | FileSystemRights.ChangePermissions
            | FileSystemRights.ReadAttributes
            | FileSystemRights.ReadExtendedAttributes
            | FileSystemRights.Synchronize;
    }

    private static bool IsReparsePoint(FileSystemInfo item)
    {
        return (item.Attributes & FileAttributes.ReparsePoint) == FileAttributes.ReparsePoint;
    }

    private static void RemoveAllAccessRules(FileSystemSecurity security)
    {
        AuthorizationRuleCollection rules = security.GetAccessRules(
            includeExplicit: true,
            includeInherited: true,
            targetType: typeof(SecurityIdentifier));

        foreach (FileSystemAccessRule rule in rules.Cast<FileSystemAccessRule>().ToArray())
        {
            security.RemoveAccessRuleSpecific(rule);
        }
    }

    private static void AddInheritedRule(
        DirectorySecurity security,
        WellKnownSidType sidType,
        FileSystemRights rights)
    {
        AddInheritedRule(security, new SecurityIdentifier(sidType, null), rights);
    }

    private static void AddFileRule(
        FileSecurity security,
        WellKnownSidType sidType,
        FileSystemRights rights)
    {
        AddFileRule(security, new SecurityIdentifier(sidType, null), rights);
    }

    private static void AddFileRule(
        FileSecurity security,
        IdentityReference identity,
        FileSystemRights rights)
    {
        security.AddAccessRule(new FileSystemAccessRule(
            identity,
            rights,
            AccessControlType.Allow));
    }

    private static void AddInheritedRule(
        DirectorySecurity security,
        IdentityReference identity,
        FileSystemRights rights)
    {
        security.AddAccessRule(new FileSystemAccessRule(
            identity,
            rights,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow));
    }

    private static bool HasUserDataAccess(DirectorySecurity security, SecurityIdentifier currentUser)
    {
        AuthorizationRuleCollection rules = security.GetAccessRules(
            includeExplicit: true,
            includeInherited: true,
            targetType: typeof(SecurityIdentifier));

        foreach (FileSystemAccessRule rule in rules)
        {
            if (rule.AccessControlType != AccessControlType.Allow)
            {
                continue;
            }

            if (!currentUser.Equals(rule.IdentityReference))
            {
                continue;
            }

            FileSystemRights rights = rule.FileSystemRights;

            if (rights.HasFlag(FileSystemRights.FullControl)
                || rights.HasFlag(FileSystemRights.Modify)
                || rights.HasFlag(FileSystemRights.ReadAndExecute)
                || rights.HasFlag(FileSystemRights.ListDirectory)
                || rights.HasFlag(FileSystemRights.ReadData))
            {
                return true;
            }
        }

        return false;
    }

    private static string CreateOperation(string reason, CryptAccessMode mode)
    {
        string operationId = Guid.NewGuid().ToString("N");
        string operationPath = GetOperationPath(operationId);
        OperationState operation = new(
            DateTimeOffset.UtcNow,
            NormalizeReason(reason),
            mode.ToString().ToLowerInvariant());

        File.WriteAllText(operationPath, JsonSerializer.Serialize(operation, JsonOptions));
        return operationId;
    }

    private static void DeleteOperation(string operationId)
    {
        string operationPath = GetOperationPath(operationId);

        if (File.Exists(operationPath))
        {
            File.Delete(operationPath);
        }
    }

    private static bool HasActiveOperations()
    {
        CleanupStaleOperations();
        return Directory.EnumerateFiles(OperationDirectory, "*.json", SearchOption.TopDirectoryOnly).Any();
    }

    private static void CleanupStaleOperations()
    {
        if (!Directory.Exists(OperationDirectory))
        {
            return;
        }

        DateTimeOffset cutoff = DateTimeOffset.UtcNow.Subtract(StaleOperationAge);

        foreach (string operationPath in Directory.EnumerateFiles(OperationDirectory, "*.json", SearchOption.TopDirectoryOnly))
        {
            try
            {
                OperationState? operation = JsonSerializer.Deserialize<OperationState>(
                    File.ReadAllText(operationPath),
                    JsonOptions);

                if (operation is null || operation.CreatedUtc < cutoff)
                {
                    File.Delete(operationPath);
                }
            }
            catch
            {
                File.Delete(operationPath);
            }
        }
    }

    private static AccessState ReadState()
    {
        try
        {
            if (!File.Exists(StatePath))
            {
                return new AccessState(null);
            }

            return JsonSerializer.Deserialize<AccessState>(File.ReadAllText(StatePath), JsonOptions)
                ?? new AccessState(null);
        }
        catch
        {
            return new AccessState(null);
        }
    }

    private static void WriteState(AccessState state)
    {
        Directory.CreateDirectory(StateDirectory);
        File.WriteAllText(StatePath, JsonSerializer.Serialize(state, JsonOptions));
    }

    private static void ClearState()
    {
        if (File.Exists(StatePath))
        {
            File.Delete(StatePath);
        }
    }

    private static bool IsUserUnlockActive(AccessState state)
    {
        return state.UnlockedUntilUtc is { } unlockedUntilUtc
            && unlockedUntilUtc > DateTimeOffset.UtcNow;
    }

    private static void SetLockedAttributes(string directoryPath)
    {
        try
        {
            FileAttributes attributes = File.GetAttributes(directoryPath);
            File.SetAttributes(directoryPath, attributes | FileAttributes.Hidden);
        }
        catch (Exception ex)
        {
            Program.Log($"Crypt locked attribute update skipped: {ex.Message}");
        }
    }

    private static void SetUnlockedAttributes(string directoryPath)
    {
        try
        {
            FileAttributes attributes = File.GetAttributes(directoryPath);
            File.SetAttributes(directoryPath, attributes & ~FileAttributes.Hidden);
        }
        catch (Exception ex)
        {
            Program.Log($"Crypt unlocked attribute update skipped: {ex.Message}");
        }
    }

    private static SecurityIdentifier GetCurrentUserSid()
    {
        return WindowsIdentity.GetCurrent().User
            ?? throw new InvalidOperationException("DLP could not resolve the current Windows user");
    }

    private static T WithAclLock<T>(Func<T> action)
    {
        using Mutex mutex = new(initiallyOwned: false, MutexName);
        bool lockTaken = false;

        try
        {
            try
            {
                lockTaken = mutex.WaitOne(TimeSpan.FromSeconds(10));
            }
            catch (AbandonedMutexException)
            {
                lockTaken = true;
            }

            if (!lockTaken)
            {
                throw new TimeoutException("Timed out waiting for the DLP folder security lock");
            }

            return action();
        }
        finally
        {
            if (lockTaken)
            {
                mutex.ReleaseMutex();
            }
        }
    }

    private static string NormalizeReason(string reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            return "unknown";
        }

        string normalized = reason.Trim();
        return normalized.Length <= 80 ? normalized : normalized[..80];
    }

    private static string StateDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DLP",
        "access");

    private static string StatePath => Path.Combine(StateDirectory, "download-folder.json");

    private static string OperationDirectory => Path.Combine(StateDirectory, "operations");

    private static string GetOperationPath(string operationId) => Path.Combine(OperationDirectory, $"{operationId}.json");

    private sealed record AccessState(DateTimeOffset? UnlockedUntilUtc);

    private sealed record OperationState(DateTimeOffset CreatedUtc, string Reason, string Mode);
}

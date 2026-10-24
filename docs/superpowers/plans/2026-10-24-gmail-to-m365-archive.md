# Gmail to Microsoft 365 Archive Mover Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build a .NET 10 console app that moves the oldest Gmail messages into a Microsoft 365 mailbox over IMAP, verifies each one, trashes the Gmail copy, and records every step in a SQL Server ledger.

**Architecture:** A core library holds all logic behind three interfaces (`IMailSource`, `IMailTarget`, `ILedger`) so every workflow is unit-tested with in-memory fakes. MailKit implementations of the mail interfaces and an EF Core implementation of the ledger live in the same library but carry no logic beyond protocol handling. A thin CLI wires configuration, logging, and commands.

**Tech Stack:** C# / .NET 10, MailKit 4.18, Microsoft.Identity.Client (MSAL) 4.91 with the MSAL cache extension, EF Core 10 on SQL Server, Serilog, System.CommandLine 2.0, xUnit 2.9, Meziantou.Framework.Win32.CredentialManager.

**Spec:** `docs/superpowers/specs/2026-10-24-gmail-to-m365-archive-design.md`

## Global Constraints

- Target framework `net10.0` for every project. Nullable and implicit usings enabled.
- The repository is public. No real email address, tenant ID, client ID, folder listing, or connection string may appear in any tracked file. Placeholders only: `you@gmail.com`, `you@yourdomain.com`, all-zero GUIDs.
- All user-specific values come from `appsettings.local.json`, which is git-ignored.
- The Gmail app password is stored only in Windows Credential Manager. The Microsoft token cache is written only by the MSAL cache extension.
- Delete semantics: Gmail messages are moved to `[Gmail]/Trash`. Expunge happens only when `--purge` is passed. Default off.
- No Gmail message is trashed unless its ledger row reached `Verified`.
- Every ledger transition is saved before the next network call.
- Three consecutive per-message failures abort the run.
- Exit codes: 0 success, 1 partial (some failures), 2 aborted or configuration error, 3 Microsoft sign-in required.
- Default target folders: `Gmail Archive` and `Gmail Archive/Sent`. Everything else flat.
- Selection: `InternalDate` strictly before the cutoff, where cutoff is UTC midnight today minus `KeepYears` (default 5). Oldest first. Spam and Trash are never read because only `[Gmail]/All Mail` is listed.
- Verification ignores size (warning only above 10 percent difference) because Exchange re-encodes MIME.
- The ledger database is created and migrated automatically at the start of every command that needs it (`MigrateAsync` on startup). This replaces the spec's optional `--migrate` flag; `dotnet ef database update` still works for anyone who prefers it.
- Commit after every task with a conventional-commit message and the attribution trailer shown in Task 1.

## Review Focus

1. **A second Gmail copy with the same Message-ID as an already-trashed message** (forwarded or re-delivered mail). Expected: the copy is verified against the target and trashed too, not left in Gmail forever. Test added in Task 7 (`Duplicate_copy_of_trashed_key_is_verified_and_trashed`).
2. **A message deleted from Gmail between listing and fetch** (user empties a label mid-run). Expected: that message is marked `Failed` with the reason, the run continues. Test added in Task 7 (`Fetch_failure_marks_failed_and_continues`).
3. **The first selected message is larger than `--max-bytes`.** Expected: it still moves so the run always makes progress; the cap stops before the second message. Test added in Task 4 (`MaxBytes_always_allows_first_message`).
4. **Empty From or empty Subject** (bounces, calendar notices). Expected: verification still matches empty to empty and does not throw. Tests added in Task 5 (`Empty_from_and_subject_match`) and Task 3 (`Fallback_key_handles_empty_fields`).
5. **Microsoft token expires mid-run.** Expected: the run stops with exit code 3 and does not count the auth failure as a message failure or keep retrying. Test added in Task 7 (`Auth_required_during_reconnect_propagates`).

## File Structure

```
EmailArchive.sln
LICENSE
README.md
.gitignore
docs/
  superpowers/specs/2026-10-24-gmail-to-m365-archive-design.md
  superpowers/plans/2026-10-24-gmail-to-m365-archive.md   (this file)
  setup-entra.md            click-through for the app registration
  setup-gmail.md            app password steps
  setup-schedule.md         Task Scheduler command
src/EmailArchive.Core/
  EmailArchive.Core.csproj
  Config/AppSettings.cs           settings classes + Validate()
  Config/ConfigurationException.cs
  Model/Enums.cs                  KeyType, MessageState, TargetFolder, RunMode
  Model/MessageKey.cs             MessageKey record + MessageKeyBuilder
  Model/MessageInfo.cs            SourceMessageInfo, TargetMessageInfo
  Model/HeaderNormalizer.cs
  Selection/CutoffCalculator.cs
  Selection/MessageSelector.cs    SelectionOptions, SelectedMessage, Select()
  Selection/GmailLabels.cs        IsSent / IsChat from label strings
  Selection/FolderRouter.cs
  Verification/Verifier.cs        VerifyResult, Verify()
  Ledger/LedgerMessage.cs         EF entity
  Ledger/LedgerRun.cs             EF entity
  Ledger/ILedger.cs
  Ledger/LedgerRules.cs           pure functions shared by SQL and fake ledgers
  Ledger/EmailArchiveDbContext.cs
  Ledger/DesignTimeDbContextFactory.cs
  Ledger/SqlLedger.cs
  Ledger/Migrations/              generated
  Mail/IMailSource.cs
  Mail/IMailTarget.cs
  Mail/GmailSource.cs             MailKit, app password
  Mail/M365Target.cs              MailKit, OAuth2
  Auth/AuthRequiredException.cs
  Auth/MicrosoftAuth.cs           MSAL public client + cache
  Credentials/CredentialStore.cs  Windows Credential Manager
  Workflows/RunSummary.cs
  Workflows/MoveWorkflow.cs
  Workflows/ReconcileWorkflow.cs
  Workflows/ReportWorkflow.cs
src/EmailArchive.Cli/
  EmailArchive.Cli.csproj
  Program.cs                      System.CommandLine tree only
  AppHost.cs                      config, logging, factories, exit-code mapping
  Commands.cs                     one method per command
  appsettings.json                placeholders only
tests/EmailArchive.Tests/
  EmailArchive.Tests.csproj
  Fakes/FakeSource.cs
  Fakes/FakeTarget.cs
  Fakes/InMemoryLedger.cs
  TestData.cs                     Msg() and MakeMime() helpers
  Config/AppSettingsTests.cs
  Selection/CutoffCalculatorTests.cs
  Model/MessageKeyBuilderTests.cs
  Model/HeaderNormalizerTests.cs
  Selection/MessageSelectorTests.cs
  Selection/GmailLabelsTests.cs
  Verification/VerifierTests.cs
  Ledger/LedgerRulesTests.cs
  Ledger/SqlLedgerTests.cs        gated on EMAILARCHIVE_TEST_CONNECTION
  Workflows/MoveWorkflowTests.cs
  Workflows/ReconcileWorkflowTests.cs
  Workflows/ReportWorkflowTests.cs
```

---

### Task 1: Solution scaffold

**Files:**
- Create: `EmailArchive.sln`, `LICENSE`
- Create: `src/EmailArchive.Core/EmailArchive.Core.csproj`
- Create: `src/EmailArchive.Cli/EmailArchive.Cli.csproj`, `src/EmailArchive.Cli/Program.cs` (placeholder, replaced in Task 12)
- Create: `tests/EmailArchive.Tests/EmailArchive.Tests.csproj`, `tests/EmailArchive.Tests/SmokeTests.cs`

**Interfaces:**
- Produces: three projects with package references every later task relies on.

- [ ] **Step 1: Create projects and solution**

Run from the repo root:

```powershell
dotnet new sln -n EmailArchive
dotnet new classlib -n EmailArchive.Core -o src/EmailArchive.Core -f net10.0
dotnet new console -n EmailArchive.Cli -o src/EmailArchive.Cli -f net10.0
dotnet new xunit -n EmailArchive.Tests -o tests/EmailArchive.Tests -f net10.0
dotnet sln add src/EmailArchive.Core src/EmailArchive.Cli tests/EmailArchive.Tests
dotnet add src/EmailArchive.Cli reference src/EmailArchive.Core
dotnet add tests/EmailArchive.Tests reference src/EmailArchive.Core
Remove-Item src/EmailArchive.Core/Class1.cs
Remove-Item tests/EmailArchive.Tests/UnitTest1.cs
```

- [ ] **Step 2: Add packages**

```powershell
dotnet add src/EmailArchive.Core package MailKit
dotnet add src/EmailArchive.Core package Microsoft.EntityFrameworkCore.SqlServer
dotnet add src/EmailArchive.Core package Microsoft.Extensions.Logging.Abstractions
dotnet add src/EmailArchive.Core package Microsoft.Identity.Client
dotnet add src/EmailArchive.Core package Microsoft.Identity.Client.Extensions.Msal
dotnet add src/EmailArchive.Core package Meziantou.Framework.Win32.CredentialManager

dotnet add src/EmailArchive.Cli package System.CommandLine
dotnet add src/EmailArchive.Cli package Serilog
dotnet add src/EmailArchive.Cli package Serilog.Sinks.Console
dotnet add src/EmailArchive.Cli package Serilog.Sinks.File
dotnet add src/EmailArchive.Cli package Serilog.Extensions.Logging
dotnet add src/EmailArchive.Cli package Microsoft.Extensions.Configuration.Json
dotnet add src/EmailArchive.Cli package Microsoft.Extensions.Configuration.Binder
dotnet add src/EmailArchive.Cli package Microsoft.EntityFrameworkCore.Design

dotnet add tests/EmailArchive.Tests package Microsoft.EntityFrameworkCore.SqlServer
```

Expected latest stable versions at plan time: MailKit 4.18.2, Microsoft.Identity.Client 4.91.1, EF Core 10.0.13, System.CommandLine 2.0.13, Serilog 4.4.0, Meziantou.Framework.Win32.CredentialManager 3.0.6. Unpinned `add package` picks the latest stable, which is fine.

- [ ] **Step 3: Write the smoke test**

`tests/EmailArchive.Tests/SmokeTests.cs`:

```csharp
namespace EmailArchive.Tests;

public class SmokeTests
{
    [Fact]
    public void Test_project_builds_and_runs()
    {
        Assert.True(true);
    }
}
```

- [ ] **Step 4: Add the MIT license**

`LICENSE`:

```
MIT License

Copyright (c) 2026 townbackyard

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

- [ ] **Step 5: Build and run tests**

Run: `dotnet test`
Expected: 1 test passed, no warnings about missing packages.

- [ ] **Step 6: Commit**

```powershell
git add .
git commit -m "chore: scaffold solution with core, cli, and test projects

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01UptAf2FZ6KGy13ca69nGg5"
```

Every later commit in this plan uses the same two trailer lines.

---

### Task 2: Configuration

**Files:**
- Create: `src/EmailArchive.Core/Config/AppSettings.cs`
- Create: `src/EmailArchive.Core/Config/ConfigurationException.cs`
- Create: `src/EmailArchive.Core/Selection/CutoffCalculator.cs`
- Create: `src/EmailArchive.Cli/appsettings.json`
- Modify: `src/EmailArchive.Cli/EmailArchive.Cli.csproj` (copy settings files to output)
- Test: `tests/EmailArchive.Tests/Config/AppSettingsTests.cs`, `tests/EmailArchive.Tests/Selection/CutoffCalculatorTests.cs`

**Interfaces:**
- Produces: `AppSettings` with nested `GmailSettings`, `Microsoft365Settings`, `SelectionSettings`, `LedgerSettings`, `LoggingSettings`; `AppSettings.Validate()` throws `ConfigurationException`; `CutoffCalculator.Compute(DateTimeOffset now, int keepYears)`.

- [ ] **Step 1: Write failing tests**

`tests/EmailArchive.Tests/Config/AppSettingsTests.cs`:

```csharp
using EmailArchive.Core.Config;

namespace EmailArchive.Tests.Config;

public class AppSettingsTests
{
    private static AppSettings Valid() => new()
    {
        Gmail = new GmailSettings { Address = "someone@gmail.com" },
        Microsoft365 = new Microsoft365Settings
        {
            Address = "someone@example.org",
            TenantId = "11111111-2222-3333-4444-555555555555",
            ClientId = "66666666-7777-8888-9999-000000000000",
        },
        Ledger = new LedgerSettings { ConnectionString = "Server=localhost;Database=X;Trusted_Connection=True" },
    };

    [Fact]
    public void Valid_settings_pass()
    {
        Valid().Validate();
    }

    [Fact]
    public void Placeholder_tenant_id_is_rejected()
    {
        var s = Valid();
        s.Microsoft365.TenantId = "00000000-0000-0000-0000-000000000000";
        var ex = Assert.Throws<ConfigurationException>(() => s.Validate());
        Assert.Contains("TenantId", ex.Message);
    }

    [Fact]
    public void Placeholder_addresses_are_rejected()
    {
        var s = Valid();
        s.Gmail.Address = "you@gmail.com";
        s.Microsoft365.Address = "you@yourdomain.com";
        var ex = Assert.Throws<ConfigurationException>(() => s.Validate());
        Assert.Contains("Gmail:Address", ex.Message);
        Assert.Contains("Microsoft365:Address", ex.Message);
    }

    [Fact]
    public void Negative_keep_years_is_rejected()
    {
        var s = Valid();
        s.Selection.KeepYears = -1;
        Assert.Throws<ConfigurationException>(() => s.Validate());
    }

    [Fact]
    public void Defaults_are_sensible()
    {
        var s = new AppSettings();
        Assert.Equal("imap.gmail.com", s.Gmail.ImapHost);
        Assert.Equal(993, s.Gmail.ImapPort);
        Assert.Equal("outlook.office365.com", s.Microsoft365.ImapHost);
        Assert.Equal("Gmail Archive", s.Microsoft365.ArchiveFolder);
        Assert.Equal("Gmail Archive/Sent", s.Microsoft365.SentFolder);
        Assert.Equal(5, s.Selection.KeepYears);
        Assert.False(s.Selection.ExcludeChats);
        Assert.Equal("logs", s.Logging.Directory);
    }
}
```

`tests/EmailArchive.Tests/Selection/CutoffCalculatorTests.cs`:

```csharp
using EmailArchive.Core.Selection;

namespace EmailArchive.Tests.Selection;

public class CutoffCalculatorTests
{
    [Fact]
    public void Cutoff_is_utc_midnight_minus_years()
    {
        var now = new DateTimeOffset(2026, 10, 24, 15, 30, 0, TimeSpan.FromHours(-4));
        var cutoff = CutoffCalculator.Compute(now, 5);
        Assert.Equal(new DateTimeOffset(2021, 10, 24, 0, 0, 0, TimeSpan.Zero), cutoff);
    }

    [Fact]
    public void Zero_years_means_today()
    {
        var now = new DateTimeOffset(2026, 2, 1, 1, 0, 0, TimeSpan.Zero);
        Assert.Equal(new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero), CutoffCalculator.Compute(now, 0));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test`
Expected: build errors, `AppSettings` and `CutoffCalculator` do not exist.

- [ ] **Step 3: Implement**

`src/EmailArchive.Core/Config/ConfigurationException.cs`:

```csharp
namespace EmailArchive.Core.Config;

public sealed class ConfigurationException(string message) : Exception(message);
```

`src/EmailArchive.Core/Config/AppSettings.cs`:

```csharp
namespace EmailArchive.Core.Config;

public sealed class AppSettings
{
    public GmailSettings Gmail { get; set; } = new();
    public Microsoft365Settings Microsoft365 { get; set; } = new();
    public SelectionSettings Selection { get; set; } = new();
    public LedgerSettings Ledger { get; set; } = new();
    public LoggingSettings Logging { get; set; } = new();

    private const string ZeroGuid = "00000000-0000-0000-0000-000000000000";

    public void Validate()
    {
        var problems = new List<string>();

        if (string.IsNullOrWhiteSpace(Gmail.Address) || Gmail.Address == "you@gmail.com")
            problems.Add("Gmail:Address must be your real Gmail address");
        if (string.IsNullOrWhiteSpace(Microsoft365.Address) || Microsoft365.Address == "you@yourdomain.com")
            problems.Add("Microsoft365:Address must be your real Microsoft 365 address");
        if (!Guid.TryParse(Microsoft365.TenantId, out _) || Microsoft365.TenantId == ZeroGuid)
            problems.Add("Microsoft365:TenantId must be the Directory (tenant) ID from your app registration");
        if (!Guid.TryParse(Microsoft365.ClientId, out _) || Microsoft365.ClientId == ZeroGuid)
            problems.Add("Microsoft365:ClientId must be the Application (client) ID from your app registration");
        if (string.IsNullOrWhiteSpace(Microsoft365.ArchiveFolder))
            problems.Add("Microsoft365:ArchiveFolder must not be empty");
        if (string.IsNullOrWhiteSpace(Microsoft365.SentFolder))
            problems.Add("Microsoft365:SentFolder must not be empty");
        if (Selection.KeepYears < 0)
            problems.Add("Selection:KeepYears must be zero or more");
        if (string.IsNullOrWhiteSpace(Ledger.ConnectionString))
            problems.Add("Ledger:ConnectionString must not be empty");

        if (problems.Count > 0)
            throw new ConfigurationException(
                "Configuration problems (fix them in appsettings.local.json):\n  " + string.Join("\n  ", problems));
    }
}

public sealed class GmailSettings
{
    public string Address { get; set; } = "you@gmail.com";
    public string ImapHost { get; set; } = "imap.gmail.com";
    public int ImapPort { get; set; } = 993;
}

public sealed class Microsoft365Settings
{
    public string Address { get; set; } = "you@yourdomain.com";
    public string TenantId { get; set; } = "00000000-0000-0000-0000-000000000000";
    public string ClientId { get; set; } = "00000000-0000-0000-0000-000000000000";
    public string ImapHost { get; set; } = "outlook.office365.com";
    public int ImapPort { get; set; } = 993;
    public string ArchiveFolder { get; set; } = "Gmail Archive";
    public string SentFolder { get; set; } = "Gmail Archive/Sent";
}

public sealed class SelectionSettings
{
    public int KeepYears { get; set; } = 5;
    public bool ExcludeChats { get; set; }
}

public sealed class LedgerSettings
{
    public string ConnectionString { get; set; } =
        "Server=localhost;Database=EmailArchive;Trusted_Connection=True;TrustServerCertificate=True";
}

public sealed class LoggingSettings
{
    public string Directory { get; set; } = "logs";
}
```

`src/EmailArchive.Core/Selection/CutoffCalculator.cs`:

```csharp
namespace EmailArchive.Core.Selection;

public static class CutoffCalculator
{
    /// <summary>UTC midnight of today's date (in UTC) minus the given number of years.</summary>
    public static DateTimeOffset Compute(DateTimeOffset now, int keepYears)
    {
        var todayUtc = new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero);
        return todayUtc.AddYears(-keepYears);
    }
}
```

`src/EmailArchive.Cli/appsettings.json`:

```json
{
  "Gmail": {
    "Address": "you@gmail.com",
    "ImapHost": "imap.gmail.com",
    "ImapPort": 993
  },
  "Microsoft365": {
    "Address": "you@yourdomain.com",
    "TenantId": "00000000-0000-0000-0000-000000000000",
    "ClientId": "00000000-0000-0000-0000-000000000000",
    "ImapHost": "outlook.office365.com",
    "ImapPort": 993,
    "ArchiveFolder": "Gmail Archive",
    "SentFolder": "Gmail Archive/Sent"
  },
  "Selection": {
    "KeepYears": 5,
    "ExcludeChats": false
  },
  "Ledger": {
    "ConnectionString": "Server=localhost;Database=EmailArchive;Trusted_Connection=True;TrustServerCertificate=True"
  },
  "Logging": {
    "Directory": "logs"
  }
}
```

Add to `src/EmailArchive.Cli/EmailArchive.Cli.csproj` inside `<Project>`:

```xml
<ItemGroup>
  <None Update="appsettings.json" CopyToOutputDirectory="PreserveNewest" />
  <None Update="appsettings.local.json" CopyToOutputDirectory="PreserveNewest" Condition="Exists('appsettings.local.json')" />
</ItemGroup>
```

- [ ] **Step 4: Run tests**

Run: `dotnet test`
Expected: all pass (1 smoke + 5 settings + 2 cutoff).

- [ ] **Step 5: Commit**

```powershell
git add .
git commit -m "feat: add configuration model, validation, and cutoff calculator"
```
(append the two attribution trailer lines)

---
### Task 3: Model, message keys, and header normalization

**Files:**
- Create: `src/EmailArchive.Core/Model/Enums.cs`
- Create: `src/EmailArchive.Core/Model/MessageInfo.cs`
- Create: `src/EmailArchive.Core/Model/HeaderNormalizer.cs`
- Create: `src/EmailArchive.Core/Model/MessageKey.cs`
- Test: `tests/EmailArchive.Tests/Model/HeaderNormalizerTests.cs`, `tests/EmailArchive.Tests/Model/MessageKeyBuilderTests.cs`

**Interfaces:**
- Produces:
  - `enum KeyType { MessageId, Fallback }`, `enum MessageState { Pending, AlreadyInTarget, Copied, Verified, Trashed, Purged, Failed }`, `enum TargetFolder { Archive, Sent }`, `enum RunMode { Report, Reconcile, Move }`
  - `record SourceMessageInfo(uint Uid, string? MessageId, DateTimeOffset InternalDate, DateTimeOffset? HeaderDate, string From, string Subject, long Size, bool IsSent, bool IsChat, bool IsSeen, bool IsFlagged)`
  - `record TargetMessageInfo(TargetFolder Folder, uint Uid, string? MessageId, DateTimeOffset? HeaderDate, string From, string Subject, long Size)`
  - `record MessageKey(KeyType Type, string Value)`
  - `MessageKeyBuilder.Build(string? messageId, DateTimeOffset? headerDate, string from, string subject)`, `MessageKeyBuilder.For(SourceMessageInfo)`, `MessageKeyBuilder.For(TargetMessageInfo)`, `MessageKeyBuilder.NormalizeMessageId(string?)`
  - `HeaderNormalizer.NormalizeSubject(string?)`, `HeaderNormalizer.NormalizeAddress(string?)`, `HeaderNormalizer.DatesMatch(DateTimeOffset?, DateTimeOffset?)`

- [ ] **Step 1: Write failing tests**

`tests/EmailArchive.Tests/Model/HeaderNormalizerTests.cs`:

```csharp
using EmailArchive.Core.Model;

namespace EmailArchive.Tests.Model;

public class HeaderNormalizerTests
{
    [Theory]
    [InlineData(null, "")]
    [InlineData("   ", "")]
    [InlineData("  Re:   hello\r\n world ", "Re: hello world")]
    public void NormalizeSubject_collapses_whitespace(string? input, string expected)
    {
        Assert.Equal(expected, HeaderNormalizer.NormalizeSubject(input));
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData("Jane Doe <Jane@Example.COM>", "jane@example.com")]
    [InlineData("jane@example.com", "jane@example.com")]
    [InlineData("  JANE@example.com  ", "jane@example.com")]
    public void NormalizeAddress_extracts_lowercase_address(string? input, string expected)
    {
        Assert.Equal(expected, HeaderNormalizer.NormalizeAddress(input));
    }

    [Fact]
    public void DatesMatch_within_two_minutes()
    {
        var a = new DateTimeOffset(2010, 1, 1, 12, 0, 0, TimeSpan.Zero);
        Assert.True(HeaderNormalizer.DatesMatch(a, a.AddSeconds(90)));
        Assert.True(HeaderNormalizer.DatesMatch(a, a.ToOffset(TimeSpan.FromHours(-5))));
        Assert.False(HeaderNormalizer.DatesMatch(a, a.AddMinutes(3)));
    }

    [Fact]
    public void DatesMatch_null_only_matches_null()
    {
        var a = new DateTimeOffset(2010, 1, 1, 12, 0, 0, TimeSpan.Zero);
        Assert.True(HeaderNormalizer.DatesMatch(null, null));
        Assert.False(HeaderNormalizer.DatesMatch(a, null));
        Assert.False(HeaderNormalizer.DatesMatch(null, a));
    }
}
```

`tests/EmailArchive.Tests/Model/MessageKeyBuilderTests.cs`:

```csharp
using EmailArchive.Core.Model;

namespace EmailArchive.Tests.Model;

public class MessageKeyBuilderTests
{
    private static readonly DateTimeOffset Date = new(2009, 5, 6, 7, 8, 9, TimeSpan.Zero);

    [Fact]
    public void Message_id_key_strips_angle_brackets_and_whitespace()
    {
        var key = MessageKeyBuilder.Build(" <abc@example.com> ", Date, "a@b.c", "s");
        Assert.Equal(KeyType.MessageId, key.Type);
        Assert.Equal("abc@example.com", key.Value);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("<>")]
    public void Missing_message_id_uses_fallback_hash(string? messageId)
    {
        var key = MessageKeyBuilder.Build(messageId, Date, "a@b.c", "s");
        Assert.Equal(KeyType.Fallback, key.Type);
        Assert.Equal(64, key.Value.Length);
    }

    [Fact]
    public void Fallback_is_stable_across_header_formatting()
    {
        var k1 = MessageKeyBuilder.Build(null, Date, "Jane <JANE@x.com>", "Hi  there");
        var k2 = MessageKeyBuilder.Build(null, Date.ToOffset(TimeSpan.FromHours(2)), "jane@x.com", "Hi there");
        Assert.Equal(k1.Value, k2.Value);
    }

    [Fact]
    public void Fallback_differs_when_subject_differs()
    {
        var k1 = MessageKeyBuilder.Build(null, Date, "a@b.c", "one");
        var k2 = MessageKeyBuilder.Build(null, Date, "a@b.c", "two");
        Assert.NotEqual(k1.Value, k2.Value);
    }

    [Fact]
    public void Fallback_key_handles_empty_fields()
    {
        var key = MessageKeyBuilder.Build(null, null, "", "");
        Assert.Equal(KeyType.Fallback, key.Type);
        Assert.Equal(64, key.Value.Length);
    }

    [Fact]
    public void Source_and_target_with_same_headers_share_a_key()
    {
        var src = new SourceMessageInfo(1, "<m@x>", Date, Date, "a@b.c", "s", 10, false, false, true, false);
        var tgt = new TargetMessageInfo(TargetFolder.Archive, 9, "m@x", Date, "a@b.c", "s", 12);
        Assert.Equal(MessageKeyBuilder.For(src), MessageKeyBuilder.For(tgt));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test`
Expected: build errors for missing types.

- [ ] **Step 3: Implement**

`src/EmailArchive.Core/Model/Enums.cs`:

```csharp
namespace EmailArchive.Core.Model;

public enum KeyType { MessageId, Fallback }

public enum MessageState { Pending, AlreadyInTarget, Copied, Verified, Trashed, Purged, Failed }

public enum TargetFolder { Archive, Sent }

public enum RunMode { Report, Reconcile, Move }
```

`src/EmailArchive.Core/Model/MessageInfo.cs`:

```csharp
namespace EmailArchive.Core.Model;

/// <summary>Headers of a message as listed from Gmail All Mail.</summary>
public sealed record SourceMessageInfo(
    uint Uid,
    string? MessageId,
    DateTimeOffset InternalDate,
    DateTimeOffset? HeaderDate,
    string From,
    string Subject,
    long Size,
    bool IsSent,
    bool IsChat,
    bool IsSeen,
    bool IsFlagged);

/// <summary>Headers of a message as listed from a Microsoft 365 target folder.</summary>
public sealed record TargetMessageInfo(
    TargetFolder Folder,
    uint Uid,
    string? MessageId,
    DateTimeOffset? HeaderDate,
    string From,
    string Subject,
    long Size);
```

`src/EmailArchive.Core/Model/HeaderNormalizer.cs`:

```csharp
using System.Text.RegularExpressions;

namespace EmailArchive.Core.Model;

public static partial class HeaderNormalizer
{
    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"<([^>]+)>")]
    private static partial Regex AngleAddress();

    public static string NormalizeSubject(string? subject) =>
        string.IsNullOrWhiteSpace(subject) ? "" : Whitespace().Replace(subject.Trim(), " ");

    public static string NormalizeAddress(string? from)
    {
        if (string.IsNullOrWhiteSpace(from)) return "";
        var match = AngleAddress().Match(from);
        var address = match.Success ? match.Groups[1].Value : from;
        return address.Trim().ToLowerInvariant();
    }

    public static bool DatesMatch(DateTimeOffset? a, DateTimeOffset? b)
    {
        if (a is null || b is null) return a is null && b is null;
        return Math.Abs((a.Value - b.Value).TotalMinutes) <= 2;
    }
}
```

`src/EmailArchive.Core/Model/MessageKey.cs`:

```csharp
using System.Security.Cryptography;
using System.Text;

namespace EmailArchive.Core.Model;

public sealed record MessageKey(KeyType Type, string Value);

public static class MessageKeyBuilder
{
    public static MessageKey For(SourceMessageInfo m) => Build(m.MessageId, m.HeaderDate, m.From, m.Subject);

    public static MessageKey For(TargetMessageInfo m) => Build(m.MessageId, m.HeaderDate, m.From, m.Subject);

    public static MessageKey Build(string? messageId, DateTimeOffset? headerDate, string from, string subject)
    {
        var id = NormalizeMessageId(messageId);
        if (id is not null) return new MessageKey(KeyType.MessageId, id);

        var material = string.Join('|',
            headerDate?.ToUniversalTime().ToString("O") ?? "",
            HeaderNormalizer.NormalizeAddress(from),
            HeaderNormalizer.NormalizeSubject(subject));
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(material));
        return new MessageKey(KeyType.Fallback, Convert.ToHexString(hash));
    }

    public static string? NormalizeMessageId(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var trimmed = raw.Trim().TrimStart('<').TrimEnd('>').Trim();
        return trimmed.Length == 0 ? null : trimmed;
    }
}
```

- [ ] **Step 4: Run tests**

Run: `dotnet test`
Expected: all pass.

- [ ] **Step 5: Commit**

```powershell
git add .
git commit -m "feat: add message model, key builder, and header normalization"
```
(append the two attribution trailer lines)

---

### Task 4: Message selection and folder routing

**Files:**
- Create: `src/EmailArchive.Core/Selection/GmailLabels.cs`
- Create: `src/EmailArchive.Core/Selection/FolderRouter.cs`
- Create: `src/EmailArchive.Core/Selection/MessageSelector.cs`
- Create: `tests/EmailArchive.Tests/TestData.cs`
- Test: `tests/EmailArchive.Tests/Selection/GmailLabelsTests.cs`, `tests/EmailArchive.Tests/Selection/MessageSelectorTests.cs`

**Interfaces:**
- Consumes: `SourceMessageInfo`, `MessageKey`, `MessageKeyBuilder.For`, `TargetFolder` (Task 3).
- Produces:
  - `record SelectionOptions(DateTimeOffset Cutoff, int? MaxMessages, long? MaxBytes, bool ExcludeChats)`
  - `record SelectedMessage(SourceMessageInfo Message, MessageKey Key)`
  - `MessageSelector.Select(IEnumerable<SourceMessageInfo> candidates, SelectionOptions options, IReadOnlyDictionary<string, uint> completed)` returning `IReadOnlyList<SelectedMessage>`. `completed` maps ledger key value to the Gmail UID that was trashed.
  - `GmailLabels.IsSent(IEnumerable<string>)`, `GmailLabels.IsChat(IEnumerable<string>)`
  - `FolderRouter.Route(SourceMessageInfo)` returning `TargetFolder`
  - `TestData.Msg(...)` and `TestData.MakeMime(...)` helpers used by every later test.

- [ ] **Step 1: Write the test helper**

`tests/EmailArchive.Tests/TestData.cs`:

```csharp
using EmailArchive.Core.Model;
using MimeKit;

namespace EmailArchive.Tests;

public static class TestData
{
    public static SourceMessageInfo Msg(
        uint uid,
        string date,
        string? messageId = "default",
        string from = "sender@example.com",
        string subject = "Hello",
        long size = 1000,
        bool sent = false,
        bool chat = false,
        bool seen = true,
        bool flagged = false)
    {
        var d = DateTimeOffset.Parse(date, null, System.Globalization.DateTimeStyles.AssumeUniversal);
        var id = messageId == "default" ? $"id-{uid}@example.com" : messageId;
        return new SourceMessageInfo(uid, id, d, d, from, subject, size, sent, chat, seen, flagged);
    }

    public static MimeMessage MakeMime(SourceMessageInfo m)
    {
        var mime = new MimeMessage();
        mime.MessageId = m.MessageId ?? MimeUtils.GenerateMessageId();
        mime.Date = m.HeaderDate ?? m.InternalDate;
        if (!string.IsNullOrEmpty(m.From)) mime.From.Add(MailboxAddress.Parse(m.From));
        mime.Subject = m.Subject;
        mime.Body = new TextPart("plain") { Text = "body" };
        return mime;
    }

    public static TargetMessageInfo Target(SourceMessageInfo m, TargetFolder folder = TargetFolder.Archive, uint uid = 500, long? size = null) =>
        new(folder, uid, m.MessageId, m.HeaderDate, m.From, m.Subject, size ?? m.Size);
}
```

- [ ] **Step 2: Write failing tests**

`tests/EmailArchive.Tests/Selection/GmailLabelsTests.cs`:

```csharp
using EmailArchive.Core.Model;
using EmailArchive.Core.Selection;
using static EmailArchive.Tests.TestData;

namespace EmailArchive.Tests.Selection;

public class GmailLabelsTests
{
    [Theory]
    [InlineData(new[] { "\\Sent" }, true)]
    [InlineData(new[] { "\\\\Sent" }, true)]
    [InlineData(new[] { "\\Inbox", "\\Sent" }, true)]
    [InlineData(new[] { "\\Inbox" }, false)]
    [InlineData(new string[0], false)]
    public void IsSent_detects_sent_label(string[] labels, bool expected)
    {
        Assert.Equal(expected, GmailLabels.IsSent(labels));
    }

    [Fact]
    public void IsChat_detects_chat_label()
    {
        Assert.True(GmailLabels.IsChat(new[] { "\\Chat" }));
        Assert.False(GmailLabels.IsChat(new[] { "\\Inbox" }));
    }

    [Fact]
    public void Router_sends_sent_mail_to_sent_folder()
    {
        Assert.Equal(TargetFolder.Sent, FolderRouter.Route(Msg(1, "2010-01-01", sent: true)));
        Assert.Equal(TargetFolder.Archive, FolderRouter.Route(Msg(2, "2010-01-01")));
    }
}
```

`tests/EmailArchive.Tests/Selection/MessageSelectorTests.cs`:

```csharp
using EmailArchive.Core.Selection;
using static EmailArchive.Tests.TestData;

namespace EmailArchive.Tests.Selection;

public class MessageSelectorTests
{
    private static readonly DateTimeOffset Cutoff = new(2021, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly Dictionary<string, uint> None = new();

    private static SelectionOptions Opts(int? max = null, long? bytes = null, bool excludeChats = false) =>
        new(Cutoff, max, bytes, excludeChats);

    [Fact]
    public void Only_messages_strictly_before_cutoff_are_selected()
    {
        var msgs = new[]
        {
            Msg(1, "2020-12-31T23:59:59Z"),
            Msg(2, "2021-01-01T00:00:00Z"),
            Msg(3, "2021-06-01"),
        };
        var selected = MessageSelector.Select(msgs, Opts(), None);
        Assert.Equal(new uint[] { 1 }, selected.Select(s => s.Message.Uid));
    }

    [Fact]
    public void Oldest_first_then_by_uid()
    {
        var msgs = new[]
        {
            Msg(30, "2012-01-01"),
            Msg(10, "2010-01-01"),
            Msg(21, "2011-01-01"),
            Msg(20, "2011-01-01"),
        };
        var selected = MessageSelector.Select(msgs, Opts(), None);
        Assert.Equal(new uint[] { 10, 20, 21, 30 }, selected.Select(s => s.Message.Uid));
    }

    [Fact]
    public void MaxMessages_caps_the_count()
    {
        var msgs = Enumerable.Range(1, 10).Select(i => Msg((uint)i, $"2010-01-{i:00}")).ToArray();
        var selected = MessageSelector.Select(msgs, Opts(max: 3), None);
        Assert.Equal(3, selected.Count);
        Assert.Equal(new uint[] { 1, 2, 3 }, selected.Select(s => s.Message.Uid));
    }

    [Fact]
    public void MaxBytes_stops_before_exceeding()
    {
        var msgs = new[]
        {
            Msg(1, "2010-01-01", size: 400),
            Msg(2, "2010-01-02", size: 400),
            Msg(3, "2010-01-03", size: 400),
        };
        var selected = MessageSelector.Select(msgs, Opts(bytes: 1000), None);
        Assert.Equal(new uint[] { 1, 2 }, selected.Select(s => s.Message.Uid));
    }

    [Fact]
    public void MaxBytes_always_allows_first_message()
    {
        var msgs = new[]
        {
            Msg(1, "2010-01-01", size: 5_000_000),
            Msg(2, "2010-01-02", size: 10),
        };
        var selected = MessageSelector.Select(msgs, Opts(bytes: 1000), None);
        Assert.Equal(new uint[] { 1 }, selected.Select(s => s.Message.Uid));
    }

    [Fact]
    public void Completed_key_with_same_uid_is_skipped()
    {
        var m = Msg(7, "2010-01-01");
        var key = EmailArchive.Core.Model.MessageKeyBuilder.For(m);
        var completed = new Dictionary<string, uint> { [key.Value] = 7 };
        Assert.Empty(MessageSelector.Select(new[] { m }, Opts(), completed));
    }

    [Fact]
    public void Completed_key_with_different_uid_is_still_selected()
    {
        var m = Msg(8, "2010-01-01");
        var key = EmailArchive.Core.Model.MessageKeyBuilder.For(m);
        var completed = new Dictionary<string, uint> { [key.Value] = 7 };
        Assert.Single(MessageSelector.Select(new[] { m }, Opts(), completed));
    }

    [Fact]
    public void Chats_excluded_only_when_requested()
    {
        var msgs = new[] { Msg(1, "2010-01-01", chat: true), Msg(2, "2010-01-02") };
        Assert.Equal(2, MessageSelector.Select(msgs, Opts(), None).Count);
        Assert.Single(MessageSelector.Select(msgs, Opts(excludeChats: true), None));
    }

    [Fact]
    public void Selected_message_carries_its_key()
    {
        var m = Msg(1, "2010-01-01", messageId: "<x@y>");
        var selected = MessageSelector.Select(new[] { m }, Opts(), None);
        Assert.Equal("x@y", selected[0].Key.Value);
    }
}
```

- [ ] **Step 3: Run tests to verify they fail**

Run: `dotnet test`
Expected: build errors for missing types.

- [ ] **Step 4: Implement**

`src/EmailArchive.Core/Selection/GmailLabels.cs`:

```csharp
namespace EmailArchive.Core.Selection;

/// <summary>Interprets Gmail X-GM-LABELS values. System labels arrive as "\Sent", "\Inbox", "\Chat".</summary>
public static class GmailLabels
{
    public static bool IsSent(IEnumerable<string> labels) => labels.Any(l => Canonical(l) == "sent");

    public static bool IsChat(IEnumerable<string> labels) => labels.Any(l => Canonical(l) == "chat");

    private static string Canonical(string label) => label.Trim().TrimStart('\\').ToLowerInvariant();
}
```

`src/EmailArchive.Core/Selection/FolderRouter.cs`:

```csharp
using EmailArchive.Core.Model;

namespace EmailArchive.Core.Selection;

public static class FolderRouter
{
    public static TargetFolder Route(SourceMessageInfo message) =>
        message.IsSent ? TargetFolder.Sent : TargetFolder.Archive;
}
```

`src/EmailArchive.Core/Selection/MessageSelector.cs`:

```csharp
using EmailArchive.Core.Model;

namespace EmailArchive.Core.Selection;

public sealed record SelectionOptions(DateTimeOffset Cutoff, int? MaxMessages, long? MaxBytes, bool ExcludeChats);

public sealed record SelectedMessage(SourceMessageInfo Message, MessageKey Key);

public static class MessageSelector
{
    /// <param name="completed">Ledger key value to the Gmail UID already trashed or purged for that key.</param>
    public static IReadOnlyList<SelectedMessage> Select(
        IEnumerable<SourceMessageInfo> candidates,
        SelectionOptions options,
        IReadOnlyDictionary<string, uint> completed)
    {
        var result = new List<SelectedMessage>();
        long bytes = 0;

        var ordered = candidates
            .Where(m => m.InternalDate < options.Cutoff)
            .Where(m => !(options.ExcludeChats && m.IsChat))
            .OrderBy(m => m.InternalDate)
            .ThenBy(m => m.Uid);

        foreach (var message in ordered)
        {
            var key = MessageKeyBuilder.For(message);
            if (completed.TryGetValue(key.Value, out var doneUid) && doneUid == message.Uid)
                continue;

            if (options.MaxMessages is int max && result.Count >= max)
                break;
            if (options.MaxBytes is long maxBytes && result.Count > 0 && bytes + message.Size > maxBytes)
                break;

            result.Add(new SelectedMessage(message, key));
            bytes += message.Size;
        }

        return result;
    }
}
```

- [ ] **Step 5: Run tests**

Run: `dotnet test`
Expected: all pass.

- [ ] **Step 6: Commit**

```powershell
git add .
git commit -m "feat: add message selector, label detection, and folder router"
```
(append the two attribution trailer lines)

---

### Task 5: Verifier

**Files:**
- Create: `src/EmailArchive.Core/Verification/Verifier.cs`
- Test: `tests/EmailArchive.Tests/Verification/VerifierTests.cs`

**Interfaces:**
- Consumes: `SourceMessageInfo`, `TargetMessageInfo`, `HeaderNormalizer` (Task 3).
- Produces: `record VerifyResult(bool Success, string? Reason, TargetMessageInfo? Match, bool SizeWarning)`; `Verifier.Verify(SourceMessageInfo source, IReadOnlyList<TargetMessageInfo> candidates)`; `Verifier.Matches(SourceMessageInfo, TargetMessageInfo)`.

- [ ] **Step 1: Write failing tests**

`tests/EmailArchive.Tests/Verification/VerifierTests.cs`:

```csharp
using EmailArchive.Core.Model;
using EmailArchive.Core.Verification;
using static EmailArchive.Tests.TestData;

namespace EmailArchive.Tests.Verification;

public class VerifierTests
{
    [Fact]
    public void Exact_match_succeeds()
    {
        var src = Msg(1, "2010-01-01", size: 1000);
        var result = Verifier.Verify(src, new[] { Target(src, size: 1050) });
        Assert.True(result.Success);
        Assert.False(result.SizeWarning);
        Assert.Equal(500u, result.Match!.Uid);
    }

    [Fact]
    public void No_candidates_fails_with_not_found()
    {
        var src = Msg(1, "2010-01-01");
        var result = Verifier.Verify(src, Array.Empty<TargetMessageInfo>());
        Assert.False(result.Success);
        Assert.Equal("not found in target", result.Reason);
    }

    [Fact]
    public void Candidate_with_different_subject_fails()
    {
        var src = Msg(1, "2010-01-01", subject: "A");
        var bad = Target(src) with { Subject = "B" };
        var result = Verifier.Verify(src, new[] { bad });
        Assert.False(result.Success);
        Assert.Equal("candidate headers do not match", result.Reason);
    }

    [Fact]
    public void Two_matching_copies_fail()
    {
        var src = Msg(1, "2010-01-01");
        var result = Verifier.Verify(src, new[] { Target(src, uid: 1), Target(src, uid: 2) });
        Assert.False(result.Success);
        Assert.Equal("2 matching copies in target", result.Reason);
    }

    [Fact]
    public void Header_formatting_differences_still_match()
    {
        var src = Msg(1, "2010-01-01T10:00:00Z", from: "Jane <jane@x.com>", subject: "Re:  hi");
        var tgt = Target(src) with
        {
            From = "jane@x.com",
            Subject = "Re: hi",
            HeaderDate = new DateTimeOffset(2010, 1, 1, 5, 0, 30, TimeSpan.FromHours(-5)),
        };
        Assert.True(Verifier.Verify(src, new[] { tgt }).Success);
    }

    [Fact]
    public void Size_difference_over_ten_percent_warns_but_succeeds()
    {
        var src = Msg(1, "2010-01-01", size: 1000);
        var result = Verifier.Verify(src, new[] { Target(src, size: 1200) });
        Assert.True(result.Success);
        Assert.True(result.SizeWarning);
    }

    [Fact]
    public void Empty_from_and_subject_match()
    {
        var src = Msg(1, "2010-01-01", from: "", subject: "");
        var result = Verifier.Verify(src, new[] { Target(src) });
        Assert.True(result.Success);
    }

    [Fact]
    public void Null_header_date_on_both_sides_matches()
    {
        var src = Msg(1, "2010-01-01") with { HeaderDate = null };
        var result = Verifier.Verify(src, new[] { Target(src) with { HeaderDate = null } });
        Assert.True(result.Success);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test`
Expected: build errors for missing `Verifier`.

- [ ] **Step 3: Implement**

`src/EmailArchive.Core/Verification/Verifier.cs`:

```csharp
using EmailArchive.Core.Model;

namespace EmailArchive.Core.Verification;

public sealed record VerifyResult(bool Success, string? Reason, TargetMessageInfo? Match, bool SizeWarning)
{
    public static VerifyResult Ok(TargetMessageInfo match, bool sizeWarning) => new(true, null, match, sizeWarning);
    public static VerifyResult Fail(string reason) => new(false, reason, null, false);
}

public static class Verifier
{
    /// <summary>Sizes differing by more than this fraction are logged, never failed: Exchange re-encodes MIME.</summary>
    public const double SizeWarningRatio = 0.10;

    public static VerifyResult Verify(SourceMessageInfo source, IReadOnlyList<TargetMessageInfo> candidates)
    {
        var matches = candidates.Where(c => Matches(source, c)).ToList();

        if (matches.Count == 0)
            return VerifyResult.Fail(candidates.Count == 0 ? "not found in target" : "candidate headers do not match");
        if (matches.Count > 1)
            return VerifyResult.Fail($"{matches.Count} matching copies in target");

        var match = matches[0];
        var sizeWarning = source.Size > 0 && Math.Abs(match.Size - source.Size) > source.Size * SizeWarningRatio;
        return VerifyResult.Ok(match, sizeWarning);
    }

    public static bool Matches(SourceMessageInfo source, TargetMessageInfo target) =>
        HeaderNormalizer.DatesMatch(source.HeaderDate, target.HeaderDate)
        && HeaderNormalizer.NormalizeAddress(source.From) == HeaderNormalizer.NormalizeAddress(target.From)
        && HeaderNormalizer.NormalizeSubject(source.Subject) == HeaderNormalizer.NormalizeSubject(target.Subject);
}
```

- [ ] **Step 4: Run tests**

Run: `dotnet test`
Expected: all pass.

- [ ] **Step 5: Commit**

```powershell
git add .
git commit -m "feat: add verifier with header matching and size warning"
```
(append the two attribution trailer lines)

---
### Task 6: Ledger (entities, rules, EF Core, SQL implementation)

**Files:**
- Create: `src/EmailArchive.Core/Ledger/LedgerMessage.cs`, `LedgerRun.cs`, `ILedger.cs`, `LedgerRules.cs`, `EmailArchiveDbContext.cs`, `DesignTimeDbContextFactory.cs`, `SqlLedger.cs`
- Create: `src/EmailArchive.Core/Ledger/Migrations/*` (generated)
- Test: `tests/EmailArchive.Tests/Ledger/LedgerRulesTests.cs`, `tests/EmailArchive.Tests/Ledger/SqlLedgerTests.cs`

**Interfaces:**
- Consumes: enums, `MessageKey`, `SourceMessageInfo`, `TargetMessageInfo` (Task 3).
- Produces:
  - `LedgerMessage` entity (properties listed in the code below; UIDs are `long?` because SQL Server has no unsigned int).
  - `LedgerRun` entity with counters.
  - `ILedger` with: `StartRunAsync(RunMode, DateTimeOffset? cutoff, ct)`, `FinishRunAsync(LedgerRun, ct)`, `CompletedAsync(ct)` returning `IReadOnlyDictionary<string, uint>`, `FindAsync(MessageKey, ct)`, `GetOrCreateAsync(MessageKey, SourceMessageInfo, int runId, ct)`, `SaveAsync(LedgerMessage, ct)`, `RecordTargetAsync(MessageKey, TargetMessageInfo, int runId, ct)`.
  - `LedgerRules.NewFromSource(MessageKey, SourceMessageInfo, int runId)` and `LedgerRules.ApplyTarget(LedgerMessage? existing, MessageKey, TargetMessageInfo, int runId)` returning the row to save or `null` when nothing changes.
  - `SqlLedger(EmailArchiveDbContext)` implementing `ILedger`.

- [ ] **Step 1: Write failing rules tests**

`tests/EmailArchive.Tests/Ledger/LedgerRulesTests.cs`:

```csharp
using EmailArchive.Core.Ledger;
using EmailArchive.Core.Model;
using static EmailArchive.Tests.TestData;

namespace EmailArchive.Tests.Ledger;

public class LedgerRulesTests
{
    [Fact]
    public void NewFromSource_starts_pending_with_source_fields()
    {
        var m = Msg(5, "2010-03-04", from: "a@b.c", subject: "s", size: 123, sent: true);
        var key = MessageKeyBuilder.For(m);
        var row = LedgerRules.NewFromSource(key, m, runId: 9);

        Assert.Equal(MessageState.Pending, row.State);
        Assert.Equal(key.Value, row.Key);
        Assert.Equal(KeyType.MessageId, row.KeyType);
        Assert.Equal(5, row.GmailUid);
        Assert.Equal("a@b.c", row.FromAddress);
        Assert.Equal("s", row.Subject);
        Assert.Equal(123, row.SourceSize);
        Assert.True(row.IsSent);
        Assert.Equal(9, row.LastRunId);
        Assert.Null(row.TargetUid);
    }

    [Fact]
    public void ApplyTarget_creates_AlreadyInTarget_row_when_missing()
    {
        var m = Msg(1, "2010-01-01");
        var t = Target(m, TargetFolder.Sent, uid: 77, size: 900);
        var row = LedgerRules.ApplyTarget(null, MessageKeyBuilder.For(t), t, runId: 2);

        Assert.NotNull(row);
        Assert.Equal(MessageState.AlreadyInTarget, row!.State);
        Assert.Equal(TargetFolder.Sent, row.TargetFolder);
        Assert.Equal(77, row.TargetUid);
        Assert.Equal(900, row.TargetSize);
        Assert.True(row.IsSent);
        Assert.Null(row.GmailUid);
    }

    [Theory]
    [InlineData(MessageState.Pending)]
    [InlineData(MessageState.Failed)]
    public void ApplyTarget_upgrades_pending_or_failed_rows(MessageState state)
    {
        var m = Msg(1, "2010-01-01");
        var existing = LedgerRules.NewFromSource(MessageKeyBuilder.For(m), m, 1);
        existing.State = state;
        existing.FailureReason = "old";
        var t = Target(m, uid: 44);

        var row = LedgerRules.ApplyTarget(existing, MessageKeyBuilder.For(t), t, runId: 3);

        Assert.Same(existing, row);
        Assert.Equal(MessageState.AlreadyInTarget, row!.State);
        Assert.Equal(44, row.TargetUid);
        Assert.Null(row.FailureReason);
        Assert.Equal(1, row.GmailUid);
    }

    [Theory]
    [InlineData(MessageState.Copied)]
    [InlineData(MessageState.Verified)]
    [InlineData(MessageState.Trashed)]
    [InlineData(MessageState.Purged)]
    [InlineData(MessageState.AlreadyInTarget)]
    public void ApplyTarget_leaves_other_states_alone(MessageState state)
    {
        var m = Msg(1, "2010-01-01");
        var existing = LedgerRules.NewFromSource(MessageKeyBuilder.For(m), m, 1);
        existing.State = state;
        existing.TargetUid = 10;
        var t = Target(m, uid: 44);

        Assert.Null(LedgerRules.ApplyTarget(existing, MessageKeyBuilder.For(t), t, 3));
        Assert.Equal(10, existing.TargetUid);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test`
Expected: build errors for missing ledger types.

- [ ] **Step 3: Implement entities, rules, and interface**

`src/EmailArchive.Core/Ledger/LedgerMessage.cs`:

```csharp
using EmailArchive.Core.Model;

namespace EmailArchive.Core.Ledger;

public sealed class LedgerMessage
{
    public int Id { get; set; }
    public KeyType KeyType { get; set; }
    public string Key { get; set; } = "";
    public long? GmailUid { get; set; }
    public long? TrashUid { get; set; }
    public DateTimeOffset? InternalDate { get; set; }
    public string FromAddress { get; set; } = "";
    public string Subject { get; set; } = "";
    public bool IsSent { get; set; }
    public long? SourceSize { get; set; }
    public long? TargetSize { get; set; }
    public TargetFolder? TargetFolder { get; set; }
    public long? TargetUid { get; set; }
    public MessageState State { get; set; }
    public string? FailureReason { get; set; }
    public DateTimeOffset? CopiedAt { get; set; }
    public DateTimeOffset? VerifiedAt { get; set; }
    public DateTimeOffset? TrashedAt { get; set; }
    public DateTimeOffset? PurgedAt { get; set; }
    public int? LastRunId { get; set; }
}
```

`src/EmailArchive.Core/Ledger/LedgerRun.cs`:

```csharp
using EmailArchive.Core.Model;

namespace EmailArchive.Core.Ledger;

public sealed class LedgerRun
{
    public int Id { get; set; }
    public RunMode Mode { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public DateTimeOffset? CutoffDate { get; set; }
    public int Selected { get; set; }
    public int Copied { get; set; }
    public int AlreadyInTarget { get; set; }
    public int Verified { get; set; }
    public int Trashed { get; set; }
    public int Purged { get; set; }
    public int Failed { get; set; }
    public int? ExitCode { get; set; }
}
```

`src/EmailArchive.Core/Ledger/ILedger.cs`:

```csharp
using EmailArchive.Core.Model;

namespace EmailArchive.Core.Ledger;

public interface ILedger
{
    Task<LedgerRun> StartRunAsync(RunMode mode, DateTimeOffset? cutoff, CancellationToken ct);
    Task FinishRunAsync(LedgerRun run, CancellationToken ct);

    /// <summary>Key value to Gmail UID for every row in Trashed or Purged state.</summary>
    Task<IReadOnlyDictionary<string, uint>> CompletedAsync(CancellationToken ct);

    Task<LedgerMessage?> FindAsync(MessageKey key, CancellationToken ct);
    Task<LedgerMessage> GetOrCreateAsync(MessageKey key, SourceMessageInfo source, int runId, CancellationToken ct);
    Task SaveAsync(LedgerMessage message, CancellationToken ct);

    /// <summary>Reconcile upsert: records that a message exists in the target folder.</summary>
    Task RecordTargetAsync(MessageKey key, TargetMessageInfo target, int runId, CancellationToken ct);
}
```

`src/EmailArchive.Core/Ledger/LedgerRules.cs`:

```csharp
using EmailArchive.Core.Model;

namespace EmailArchive.Core.Ledger;

/// <summary>Pure ledger transitions shared by the SQL implementation and test fakes.</summary>
public static class LedgerRules
{
    public static LedgerMessage NewFromSource(MessageKey key, SourceMessageInfo source, int runId) => new()
    {
        KeyType = key.Type,
        Key = key.Value,
        GmailUid = source.Uid,
        InternalDate = source.InternalDate,
        FromAddress = source.From,
        Subject = source.Subject,
        IsSent = source.IsSent,
        SourceSize = source.Size,
        State = MessageState.Pending,
        LastRunId = runId,
    };

    /// <returns>The row to persist, or null when the existing row must not change.</returns>
    public static LedgerMessage? ApplyTarget(LedgerMessage? existing, MessageKey key, TargetMessageInfo target, int runId)
    {
        if (existing is null)
        {
            return new LedgerMessage
            {
                KeyType = key.Type,
                Key = key.Value,
                InternalDate = target.HeaderDate,
                FromAddress = target.From,
                Subject = target.Subject,
                IsSent = target.Folder == TargetFolder.Sent,
                TargetFolder = target.Folder,
                TargetUid = target.Uid,
                TargetSize = target.Size,
                State = MessageState.AlreadyInTarget,
                LastRunId = runId,
            };
        }

        if (existing.State is not (MessageState.Pending or MessageState.Failed))
            return null;

        existing.TargetFolder = target.Folder;
        existing.TargetUid = target.Uid;
        existing.TargetSize = target.Size;
        existing.State = MessageState.AlreadyInTarget;
        existing.FailureReason = null;
        existing.LastRunId = runId;
        return existing;
    }
}
```

- [ ] **Step 4: Run rules tests**

Run: `dotnet test`
Expected: all pass.

- [ ] **Step 5: Implement the DbContext and SQL ledger**

`src/EmailArchive.Core/Ledger/EmailArchiveDbContext.cs`:

```csharp
using Microsoft.EntityFrameworkCore;

namespace EmailArchive.Core.Ledger;

public sealed class EmailArchiveDbContext(DbContextOptions<EmailArchiveDbContext> options) : DbContext(options)
{
    public DbSet<LedgerMessage> Messages => Set<LedgerMessage>();
    public DbSet<LedgerRun> Runs => Set<LedgerRun>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<LedgerMessage>(e =>
        {
            e.ToTable("Messages");
            e.HasIndex(m => m.Key).IsUnique();
            e.Property(m => m.Key).HasMaxLength(512);
            e.Property(m => m.KeyType).HasConversion<string>().HasMaxLength(16);
            e.Property(m => m.State).HasConversion<string>().HasMaxLength(24);
            e.Property(m => m.TargetFolder).HasConversion<string>().HasMaxLength(16);
            e.Property(m => m.FromAddress).HasMaxLength(512);
            e.Property(m => m.Subject).HasMaxLength(2048);
            e.HasIndex(m => m.State);
        });

        modelBuilder.Entity<LedgerRun>(e =>
        {
            e.ToTable("Runs");
            e.Property(r => r.Mode).HasConversion<string>().HasMaxLength(16);
        });
    }
}
```

`src/EmailArchive.Core/Ledger/DesignTimeDbContextFactory.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace EmailArchive.Core.Ledger;

/// <summary>Used only by the dotnet-ef tool to generate migrations. The connection string is never opened for that.</summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<EmailArchiveDbContext>
{
    public EmailArchiveDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<EmailArchiveDbContext>()
            .UseSqlServer("Server=localhost;Database=EmailArchive;Trusted_Connection=True;TrustServerCertificate=True")
            .Options;
        return new EmailArchiveDbContext(options);
    }
}
```

`src/EmailArchive.Core/Ledger/SqlLedger.cs`:

```csharp
using EmailArchive.Core.Model;
using Microsoft.EntityFrameworkCore;

namespace EmailArchive.Core.Ledger;

public sealed class SqlLedger(EmailArchiveDbContext db) : ILedger
{
    public async Task<LedgerRun> StartRunAsync(RunMode mode, DateTimeOffset? cutoff, CancellationToken ct)
    {
        var run = new LedgerRun { Mode = mode, StartedAt = DateTimeOffset.UtcNow, CutoffDate = cutoff };
        db.Runs.Add(run);
        await db.SaveChangesAsync(ct);
        return run;
    }

    public async Task FinishRunAsync(LedgerRun run, CancellationToken ct)
    {
        db.Runs.Update(run);
        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyDictionary<string, uint>> CompletedAsync(CancellationToken ct)
    {
        var rows = await db.Messages
            .Where(m => (m.State == MessageState.Trashed || m.State == MessageState.Purged) && m.GmailUid != null)
            .Select(m => new { m.Key, m.GmailUid })
            .ToListAsync(ct);
        return rows.ToDictionary(r => r.Key, r => (uint)r.GmailUid!.Value);
    }

    public Task<LedgerMessage?> FindAsync(MessageKey key, CancellationToken ct) =>
        db.Messages.SingleOrDefaultAsync(m => m.Key == key.Value, ct);

    public async Task<LedgerMessage> GetOrCreateAsync(MessageKey key, SourceMessageInfo source, int runId, CancellationToken ct)
    {
        var existing = await FindAsync(key, ct);
        if (existing is not null) return existing;

        var row = LedgerRules.NewFromSource(key, source, runId);
        db.Messages.Add(row);
        await db.SaveChangesAsync(ct);
        return row;
    }

    public async Task SaveAsync(LedgerMessage message, CancellationToken ct)
    {
        db.Messages.Update(message);
        await db.SaveChangesAsync(ct);
    }

    public async Task RecordTargetAsync(MessageKey key, TargetMessageInfo target, int runId, CancellationToken ct)
    {
        var existing = await FindAsync(key, ct);
        var row = LedgerRules.ApplyTarget(existing, key, target, runId);
        if (row is null) return;
        if (existing is null) db.Messages.Add(row);
        await db.SaveChangesAsync(ct);
    }
}
```

- [ ] **Step 6: Generate the initial migration**

```powershell
dotnet tool install --global dotnet-ef
dotnet build
dotnet ef migrations add Initial --project src/EmailArchive.Core --startup-project src/EmailArchive.Cli --output-dir Ledger/Migrations
```

Expected: `src/EmailArchive.Core/Ledger/Migrations/` contains `*_Initial.cs` and the model snapshot. Open the migration and confirm tables `Messages` and `Runs` with a unique index on `Key`.

- [ ] **Step 7: Write the gated integration test**

`tests/EmailArchive.Tests/Ledger/SqlLedgerTests.cs`:

```csharp
using EmailArchive.Core.Ledger;
using EmailArchive.Core.Model;
using Microsoft.EntityFrameworkCore;
using static EmailArchive.Tests.TestData;

namespace EmailArchive.Tests.Ledger;

/// <summary>
/// Runs only when EMAILARCHIVE_TEST_CONNECTION is set, for example:
/// Server=localhost;Database=EmailArchive_Test;Trusted_Connection=True;TrustServerCertificate=True
/// The database named there is dropped and recreated.
/// </summary>
public class SqlLedgerTests
{
    private static string? Conn => Environment.GetEnvironmentVariable("EMAILARCHIVE_TEST_CONNECTION");

    private static async Task<EmailArchiveDbContext> FreshDbAsync()
    {
        var options = new DbContextOptionsBuilder<EmailArchiveDbContext>().UseSqlServer(Conn).Options;
        var db = new EmailArchiveDbContext(options);
        await db.Database.EnsureDeletedAsync();
        await db.Database.MigrateAsync();
        return db;
    }

    [Fact]
    public async Task Round_trips_message_and_run_rows()
    {
        if (Conn is null) return; // integration test, see class comment

        await using var db = await FreshDbAsync();
        var ledger = new SqlLedger(db);

        var run = await ledger.StartRunAsync(RunMode.Move, new DateTimeOffset(2021, 1, 1, 0, 0, 0, TimeSpan.Zero), CancellationToken.None);
        Assert.True(run.Id > 0);

        var m = Msg(42, "2010-01-01");
        var key = MessageKeyBuilder.For(m);
        var row = await ledger.GetOrCreateAsync(key, m, run.Id, CancellationToken.None);
        Assert.Equal(MessageState.Pending, row.State);

        row.State = MessageState.Trashed;
        await ledger.SaveAsync(row, CancellationToken.None);

        var completed = await ledger.CompletedAsync(CancellationToken.None);
        Assert.Equal(42u, completed[key.Value]);

        var again = await ledger.GetOrCreateAsync(key, m, run.Id, CancellationToken.None);
        Assert.Equal(row.Id, again.Id);

        run.Trashed = 1;
        run.FinishedAt = DateTimeOffset.UtcNow;
        await ledger.FinishRunAsync(run, CancellationToken.None);
        Assert.Equal(1, (await db.Runs.SingleAsync(r => r.Id == run.Id)).Trashed);
    }

    [Fact]
    public async Task RecordTarget_creates_and_then_leaves_row()
    {
        if (Conn is null) return; // integration test, see class comment

        await using var db = await FreshDbAsync();
        var ledger = new SqlLedger(db);
        var run = await ledger.StartRunAsync(RunMode.Reconcile, null, CancellationToken.None);

        var m = Msg(1, "2010-01-01");
        var t = Target(m, uid: 7);
        var key = MessageKeyBuilder.For(t);

        await ledger.RecordTargetAsync(key, t, run.Id, CancellationToken.None);
        var row = await ledger.FindAsync(key, CancellationToken.None);
        Assert.Equal(MessageState.AlreadyInTarget, row!.State);
        Assert.Equal(7, row.TargetUid);

        await ledger.RecordTargetAsync(key, t with { Uid = 8 }, run.Id, CancellationToken.None);
        Assert.Equal(7, (await ledger.FindAsync(key, CancellationToken.None))!.TargetUid);
    }
}
```

- [ ] **Step 8: Run tests, once without and once with the database**

Run: `dotnet test`
Expected: all pass (SQL tests return early).

Run with the local SQL Server (PowerShell):
```powershell
$env:EMAILARCHIVE_TEST_CONNECTION = "Server=localhost;Database=EmailArchive_Test;Trusted_Connection=True;TrustServerCertificate=True"
dotnet test --filter SqlLedgerTests
Remove-Item Env:EMAILARCHIVE_TEST_CONNECTION
```
Expected: 2 passed, and a database `EmailArchive_Test` now exists with tables `Messages` and `Runs`.

- [ ] **Step 9: Commit**

```powershell
git add .
git commit -m "feat: add ledger entities, rules, EF Core context, migration, and SQL implementation"
```
(append the two attribution trailer lines)

---

### Task 7: Mail interfaces, test fakes, and MoveWorkflow

**Files:**
- Create: `src/EmailArchive.Core/Mail/IMailSource.cs`, `src/EmailArchive.Core/Mail/IMailTarget.cs`
- Create: `src/EmailArchive.Core/Auth/AuthRequiredException.cs`
- Create: `src/EmailArchive.Core/Workflows/RunSummary.cs`, `src/EmailArchive.Core/Workflows/MoveWorkflow.cs`
- Create: `tests/EmailArchive.Tests/Fakes/FakeSource.cs`, `FakeTarget.cs`, `InMemoryLedger.cs`
- Test: `tests/EmailArchive.Tests/Workflows/MoveWorkflowTests.cs`

**Interfaces:**
- Consumes: everything from Tasks 3 to 6.
- Produces:
  - `IMailSource`: `ConnectAsync(ct)`, `ReconnectAsync(ct)`, `ListOlderThanAsync(DateTimeOffset cutoff, ct)` → `IReadOnlyList<SourceMessageInfo>`, `FetchAsync(uint uid, ct)` → `MimeMessage`, `TrashAsync(uint uid, ct)` → `uint?` (UID in Trash), `PurgeAsync(uint trashUid, ct)`. Extends `IAsyncDisposable`.
  - `IMailTarget`: `ConnectAsync(ct)`, `ReconnectAsync(ct)`, `EnsureFoldersAsync(ct)`, `AppendAsync(TargetFolder, MimeMessage, DateTimeOffset internalDate, bool seen, bool flagged, ct)` → `uint?`, `FindByMessageIdAsync(TargetFolder, string messageId, ct)`, `FindByFallbackAsync(TargetFolder, DateTimeOffset? headerDate, string from, string subject, ct)`, `ListAllAsync(TargetFolder, ct)`, all returning `IReadOnlyList<TargetMessageInfo>`. Extends `IAsyncDisposable`.
  - `AuthRequiredException(string message, Exception? inner = null)`.
  - `record MoveOptions(SelectionOptions Selection, bool Purge, int MaxConsecutiveFailures = 3)`.
  - `record RunSummary(int Selected, int Copied, int AlreadyInTarget, int Verified, int Trashed, int Purged, int Failed, bool Aborted)` with `int ExitCode` (2 if aborted, 1 if any failed, else 0).
  - `MoveWorkflow(IMailSource, IMailTarget, ILedger, ILogger<MoveWorkflow>)` with `RunAsync(MoveOptions, ct)` → `RunSummary`.

- [ ] **Step 1: Write interfaces and exception**

`src/EmailArchive.Core/Mail/IMailSource.cs`:

```csharp
using EmailArchive.Core.Model;
using MimeKit;

namespace EmailArchive.Core.Mail;

public interface IMailSource : IAsyncDisposable
{
    Task ConnectAsync(CancellationToken ct);
    Task ReconnectAsync(CancellationToken ct);
    Task<IReadOnlyList<SourceMessageInfo>> ListOlderThanAsync(DateTimeOffset cutoff, CancellationToken ct);
    Task<MimeMessage> FetchAsync(uint uid, CancellationToken ct);
    /// <returns>The message's UID inside the Trash folder when the server reports it.</returns>
    Task<uint?> TrashAsync(uint uid, CancellationToken ct);
    Task PurgeAsync(uint trashUid, CancellationToken ct);
}
```

`src/EmailArchive.Core/Mail/IMailTarget.cs`:

```csharp
using EmailArchive.Core.Model;
using MimeKit;

namespace EmailArchive.Core.Mail;

public interface IMailTarget : IAsyncDisposable
{
    Task ConnectAsync(CancellationToken ct);
    Task ReconnectAsync(CancellationToken ct);
    Task EnsureFoldersAsync(CancellationToken ct);
    Task<uint?> AppendAsync(TargetFolder folder, MimeMessage message, DateTimeOffset internalDate, bool seen, bool flagged, CancellationToken ct);
    Task<IReadOnlyList<TargetMessageInfo>> FindByMessageIdAsync(TargetFolder folder, string messageId, CancellationToken ct);
    Task<IReadOnlyList<TargetMessageInfo>> FindByFallbackAsync(TargetFolder folder, DateTimeOffset? headerDate, string from, string subject, CancellationToken ct);
    Task<IReadOnlyList<TargetMessageInfo>> ListAllAsync(TargetFolder folder, CancellationToken ct);
}
```

`src/EmailArchive.Core/Auth/AuthRequiredException.cs`:

```csharp
namespace EmailArchive.Core.Auth;

/// <summary>Thrown when the Microsoft sign-in must be redone interactively. Maps to exit code 3.</summary>
public sealed class AuthRequiredException(string message, Exception? inner = null) : Exception(message, inner);
```

- [ ] **Step 2: Write the fakes**

`tests/EmailArchive.Tests/Fakes/FakeSource.cs`:

```csharp
using EmailArchive.Core.Mail;
using EmailArchive.Core.Model;
using MimeKit;

namespace EmailArchive.Tests.Fakes;

public sealed class FakeSource : IMailSource
{
    public List<SourceMessageInfo> Messages { get; } = new();
    public List<uint> Trashed { get; } = new();
    public List<uint> Purged { get; } = new();
    public Func<uint, Exception?>? FetchFailure { get; set; }
    public Exception? ReconnectFailure { get; set; }
    public int ReconnectCount { get; private set; }
    public uint NextTrashUid { get; set; } = 1000;

    public Task ConnectAsync(CancellationToken ct) => Task.CompletedTask;

    public Task ReconnectAsync(CancellationToken ct)
    {
        ReconnectCount++;
        if (ReconnectFailure is not null) throw ReconnectFailure;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<SourceMessageInfo>> ListOlderThanAsync(DateTimeOffset cutoff, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<SourceMessageInfo>>(Messages.Where(m => m.InternalDate < cutoff).ToList());

    public Task<MimeMessage> FetchAsync(uint uid, CancellationToken ct)
    {
        if (FetchFailure?.Invoke(uid) is Exception ex) throw ex;
        var info = Messages.First(m => m.Uid == uid);
        return Task.FromResult(TestData.MakeMime(info));
    }

    public Task<uint?> TrashAsync(uint uid, CancellationToken ct)
    {
        Trashed.Add(uid);
        return Task.FromResult<uint?>(NextTrashUid++);
    }

    public Task PurgeAsync(uint trashUid, CancellationToken ct)
    {
        Purged.Add(trashUid);
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
```

`tests/EmailArchive.Tests/Fakes/FakeTarget.cs`:

```csharp
using EmailArchive.Core.Mail;
using EmailArchive.Core.Model;
using MimeKit;

namespace EmailArchive.Tests.Fakes;

public sealed class FakeTarget : IMailTarget
{
    public Dictionary<TargetFolder, List<TargetMessageInfo>> Folders { get; } = new()
    {
        [TargetFolder.Archive] = new(),
        [TargetFolder.Sent] = new(),
    };
    public List<(TargetFolder Folder, MimeMessage Message, DateTimeOffset Date, bool Seen, bool Flagged)> Appended { get; } = new();
    /// <summary>Lets a test corrupt what lands in the folder (wrong subject, etc.).</summary>
    public Func<TargetMessageInfo, TargetMessageInfo>? AppendTransform { get; set; }
    /// <summary>Append reports success but nothing becomes searchable.</summary>
    public bool AppendIsInvisible { get; set; }
    public Exception? AppendFailure { get; set; }
    public int ReconnectCount { get; private set; }
    private uint _nextUid = 1;

    public Task ConnectAsync(CancellationToken ct) => Task.CompletedTask;

    public Task ReconnectAsync(CancellationToken ct)
    {
        ReconnectCount++;
        return Task.CompletedTask;
    }

    public Task EnsureFoldersAsync(CancellationToken ct) => Task.CompletedTask;

    public Task<uint?> AppendAsync(TargetFolder folder, MimeMessage message, DateTimeOffset internalDate, bool seen, bool flagged, CancellationToken ct)
    {
        if (AppendFailure is not null) throw AppendFailure;
        Appended.Add((folder, message, internalDate, seen, flagged));
        var info = new TargetMessageInfo(
            folder,
            _nextUid++,
            message.MessageId,
            message.Date,
            message.From.Mailboxes.FirstOrDefault()?.Address ?? "",
            message.Subject ?? "",
            message.ToString().Length);
        if (AppendTransform is not null) info = AppendTransform(info);
        if (!AppendIsInvisible) Folders[folder].Add(info);
        return Task.FromResult<uint?>(info.Uid);
    }

    public Task<IReadOnlyList<TargetMessageInfo>> FindByMessageIdAsync(TargetFolder folder, string messageId, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<TargetMessageInfo>>(
            Folders[folder].Where(m => MessageKeyBuilder.NormalizeMessageId(m.MessageId) == messageId).ToList());

    public Task<IReadOnlyList<TargetMessageInfo>> FindByFallbackAsync(TargetFolder folder, DateTimeOffset? headerDate, string from, string subject, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<TargetMessageInfo>>(
            Folders[folder].Where(m => m.MessageId is null).ToList());

    public Task<IReadOnlyList<TargetMessageInfo>> ListAllAsync(TargetFolder folder, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<TargetMessageInfo>>(Folders[folder].ToList());

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
```

`tests/EmailArchive.Tests/Fakes/InMemoryLedger.cs`:

```csharp
using EmailArchive.Core.Ledger;
using EmailArchive.Core.Model;

namespace EmailArchive.Tests.Fakes;

public sealed class InMemoryLedger : ILedger
{
    public Dictionary<string, LedgerMessage> Rows { get; } = new();
    public List<LedgerRun> Runs { get; } = new();
    /// <summary>Every state a row passed through, in order, for assertions on save ordering.</summary>
    public List<(string Key, MessageState State)> SaveLog { get; } = new();
    private int _nextId = 1;

    public Task<LedgerRun> StartRunAsync(RunMode mode, DateTimeOffset? cutoff, CancellationToken ct)
    {
        var run = new LedgerRun { Id = Runs.Count + 1, Mode = mode, StartedAt = DateTimeOffset.UtcNow, CutoffDate = cutoff };
        Runs.Add(run);
        return Task.FromResult(run);
    }

    public Task FinishRunAsync(LedgerRun run, CancellationToken ct) => Task.CompletedTask;

    public Task<IReadOnlyDictionary<string, uint>> CompletedAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyDictionary<string, uint>>(Rows.Values
            .Where(r => r.State is MessageState.Trashed or MessageState.Purged && r.GmailUid is not null)
            .ToDictionary(r => r.Key, r => (uint)r.GmailUid!.Value));

    public Task<LedgerMessage?> FindAsync(MessageKey key, CancellationToken ct) =>
        Task.FromResult(Rows.GetValueOrDefault(key.Value));

    public Task<LedgerMessage> GetOrCreateAsync(MessageKey key, SourceMessageInfo source, int runId, CancellationToken ct)
    {
        if (Rows.TryGetValue(key.Value, out var existing)) return Task.FromResult(existing);
        var row = LedgerRules.NewFromSource(key, source, runId);
        row.Id = _nextId++;
        Rows[key.Value] = row;
        return Task.FromResult(row);
    }

    public Task SaveAsync(LedgerMessage message, CancellationToken ct)
    {
        Rows[message.Key] = message;
        SaveLog.Add((message.Key, message.State));
        return Task.CompletedTask;
    }

    public Task RecordTargetAsync(MessageKey key, TargetMessageInfo target, int runId, CancellationToken ct)
    {
        var existing = Rows.GetValueOrDefault(key.Value);
        var row = LedgerRules.ApplyTarget(existing, key, target, runId);
        if (row is null) return Task.CompletedTask;
        if (row.Id == 0) row.Id = _nextId++;
        Rows[key.Value] = row;
        return Task.CompletedTask;
    }

    /// <summary>Seeds a row as if a previous run left it in the given state.</summary>
    public LedgerMessage Seed(SourceMessageInfo source, MessageState state, Action<LedgerMessage>? tweak = null)
    {
        var key = MessageKeyBuilder.For(source);
        var row = LedgerRules.NewFromSource(key, source, 0);
        row.Id = _nextId++;
        row.State = state;
        tweak?.Invoke(row);
        Rows[key.Value] = row;
        return row;
    }
}
```

- [ ] **Step 3: Build to confirm fakes compile**

Run: `dotnet build`
Expected: success.

- [ ] **Step 4: Write failing MoveWorkflow tests**

`tests/EmailArchive.Tests/Workflows/MoveWorkflowTests.cs`:

```csharp
using EmailArchive.Core.Auth;
using EmailArchive.Core.Ledger;
using EmailArchive.Core.Model;
using EmailArchive.Core.Selection;
using EmailArchive.Core.Workflows;
using EmailArchive.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using static EmailArchive.Tests.TestData;

namespace EmailArchive.Tests.Workflows;

public class MoveWorkflowTests
{
    private static readonly DateTimeOffset Cutoff = new(2021, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly FakeSource _source = new();
    private readonly FakeTarget _target = new();
    private readonly InMemoryLedger _ledger = new();

    private MoveWorkflow Workflow() => new(_source, _target, _ledger, NullLogger<MoveWorkflow>.Instance);

    private static MoveOptions Opts(bool purge = false, int? max = null, int failures = 3) =>
        new(new SelectionOptions(Cutoff, max, null, false), purge, failures);

    private LedgerMessage Row(SourceMessageInfo m) => _ledger.Rows[MessageKeyBuilder.For(m).Value];

    [Fact]
    public async Task Happy_path_copies_verifies_and_trashes()
    {
        var m = Msg(1, "2010-01-01", seen: false, flagged: true);
        _source.Messages.Add(m);

        var summary = await Workflow().RunAsync(Opts(), CancellationToken.None);

        Assert.Equal(1, summary.Selected);
        Assert.Equal(1, summary.Copied);
        Assert.Equal(1, summary.Verified);
        Assert.Equal(1, summary.Trashed);
        Assert.Equal(0, summary.Purged);
        Assert.Equal(0, summary.Failed);
        Assert.Equal(0, summary.ExitCode);

        var appended = Assert.Single(_target.Appended);
        Assert.Equal(TargetFolder.Archive, appended.Folder);
        Assert.Equal(m.InternalDate, appended.Date);
        Assert.False(appended.Seen);
        Assert.True(appended.Flagged);

        Assert.Equal(new uint[] { 1 }, _source.Trashed);
        Assert.Empty(_source.Purged);

        var row = Row(m);
        Assert.Equal(MessageState.Trashed, row.State);
        Assert.Equal(1000, row.TrashUid);
        Assert.NotNull(row.CopiedAt);
        Assert.NotNull(row.VerifiedAt);
        Assert.NotNull(row.TrashedAt);
        Assert.Equal(new[] { MessageState.Copied, MessageState.Verified, MessageState.Trashed },
            _ledger.SaveLog.Select(s => s.State));
    }

    [Fact]
    public async Task Sent_mail_goes_to_sent_folder()
    {
        _source.Messages.Add(Msg(1, "2010-01-01", sent: true));
        await Workflow().RunAsync(Opts(), CancellationToken.None);
        Assert.Equal(TargetFolder.Sent, _target.Appended.Single().Folder);
        Assert.Single(_target.Folders[TargetFolder.Sent]);
    }

    [Fact]
    public async Task Purge_expunges_after_trash()
    {
        _source.Messages.Add(Msg(1, "2010-01-01"));
        var summary = await Workflow().RunAsync(Opts(purge: true), CancellationToken.None);
        Assert.Equal(1, summary.Purged);
        Assert.Equal(new uint[] { 1000 }, _source.Purged);
        Assert.Equal(MessageState.Purged, Row(_source.Messages[0]).State);
    }

    [Fact]
    public async Task Verify_failure_leaves_gmail_untouched()
    {
        var m = Msg(1, "2010-01-01");
        _source.Messages.Add(m);
        _target.AppendTransform = t => t with { Subject = "corrupted" };

        var summary = await Workflow().RunAsync(Opts(), CancellationToken.None);

        Assert.Equal(1, summary.Failed);
        Assert.Equal(1, summary.ExitCode);
        Assert.Empty(_source.Trashed);
        var row = Row(m);
        Assert.Equal(MessageState.Failed, row.State);
        Assert.Equal("candidate headers do not match", row.FailureReason);
    }

    [Fact]
    public async Task Invisible_append_fails_verification()
    {
        _source.Messages.Add(Msg(1, "2010-01-01"));
        _target.AppendIsInvisible = true;
        var summary = await Workflow().RunAsync(Opts(), CancellationToken.None);
        Assert.Equal(1, summary.Failed);
        Assert.Equal("not found in target", Row(_source.Messages[0]).FailureReason);
        Assert.Empty(_source.Trashed);
    }

    [Fact]
    public async Task Reconciled_row_skips_copy_and_still_trashes()
    {
        var m = Msg(1, "2010-01-01");
        _source.Messages.Add(m);
        var existing = Target(m, uid: 77);
        _target.Folders[TargetFolder.Archive].Add(existing);
        _ledger.Seed(m, MessageState.Pending, r => { r.TargetFolder = TargetFolder.Archive; r.TargetUid = 77; });

        var summary = await Workflow().RunAsync(Opts(), CancellationToken.None);

        Assert.Equal(0, summary.Copied);
        Assert.Equal(1, summary.AlreadyInTarget);
        Assert.Equal(1, summary.Trashed);
        Assert.Empty(_target.Appended);
        Assert.Equal(new uint[] { 1 }, _source.Trashed);
    }

    [Fact]
    public async Task Resumes_from_Copied_state_without_second_append()
    {
        var m = Msg(1, "2010-01-01");
        _source.Messages.Add(m);
        _target.Folders[TargetFolder.Archive].Add(Target(m, uid: 5));
        _ledger.Seed(m, MessageState.Copied, r => { r.TargetFolder = TargetFolder.Archive; r.TargetUid = 5; });

        var summary = await Workflow().RunAsync(Opts(), CancellationToken.None);

        Assert.Empty(_target.Appended);
        Assert.Equal(1, summary.Verified);
        Assert.Equal(1, summary.Trashed);
    }

    [Fact]
    public async Task Resumes_from_Verified_state_straight_to_trash()
    {
        var m = Msg(1, "2010-01-01");
        _source.Messages.Add(m);
        _ledger.Seed(m, MessageState.Verified, r => { r.TargetFolder = TargetFolder.Archive; r.TargetUid = 5; });

        var summary = await Workflow().RunAsync(Opts(), CancellationToken.None);

        Assert.Empty(_target.Appended);
        Assert.Equal(0, summary.Verified);
        Assert.Equal(1, summary.Trashed);
        Assert.Equal(new uint[] { 1 }, _source.Trashed);
    }

    [Fact]
    public async Task Failed_row_is_retried_on_next_run()
    {
        var m = Msg(1, "2010-01-01");
        _source.Messages.Add(m);
        _ledger.Seed(m, MessageState.Failed, r => r.FailureReason = "earlier");

        var summary = await Workflow().RunAsync(Opts(), CancellationToken.None);

        Assert.Equal(1, summary.Copied);
        Assert.Equal(1, summary.Trashed);
        Assert.Null(Row(m).FailureReason);
    }

    [Fact]
    public async Task Already_trashed_same_uid_is_not_selected()
    {
        var m = Msg(1, "2010-01-01");
        _source.Messages.Add(m);
        _ledger.Seed(m, MessageState.Trashed);

        var summary = await Workflow().RunAsync(Opts(), CancellationToken.None);

        Assert.Equal(0, summary.Selected);
        Assert.Empty(_source.Trashed);
    }

    [Fact]
    public async Task Duplicate_copy_of_trashed_key_is_verified_and_trashed()
    {
        var original = Msg(1, "2010-01-01", messageId: "<dup@x>");
        var copy = Msg(2, "2010-01-01", messageId: "<dup@x>");
        _source.Messages.Add(copy);
        _target.Folders[TargetFolder.Archive].Add(Target(original, uid: 9));
        _ledger.Seed(original, MessageState.Trashed, r => { r.TargetFolder = TargetFolder.Archive; r.TargetUid = 9; });

        var summary = await Workflow().RunAsync(Opts(), CancellationToken.None);

        Assert.Equal(1, summary.Selected);
        Assert.Equal(0, summary.Copied);
        Assert.Equal(1, summary.Trashed);
        Assert.Equal(new uint[] { 2 }, _source.Trashed);
        Assert.Equal(2, Row(copy).GmailUid);
    }

    [Fact]
    public async Task Fetch_failure_marks_failed_and_continues()
    {
        _source.Messages.Add(Msg(1, "2010-01-01"));
        _source.Messages.Add(Msg(2, "2010-01-02"));
        _source.FetchFailure = uid => uid == 1 ? new InvalidOperationException("The message no longer exists") : null;

        var summary = await Workflow().RunAsync(Opts(), CancellationToken.None);

        Assert.Equal(1, summary.Failed);
        Assert.Equal(1, summary.Trashed);
        Assert.Equal(new uint[] { 2 }, _source.Trashed);
        Assert.Contains("no longer exists", Row(_source.Messages[0]).FailureReason);
        Assert.Equal(1, _source.ReconnectCount);
        Assert.Equal(1, _target.ReconnectCount);
    }

    [Fact]
    public async Task Three_consecutive_failures_abort()
    {
        for (uint i = 1; i <= 5; i++) _source.Messages.Add(Msg(i, $"2010-01-0{i}"));
        _source.FetchFailure = _ => new IOException("boom");

        var summary = await Workflow().RunAsync(Opts(), CancellationToken.None);

        Assert.True(summary.Aborted);
        Assert.Equal(2, summary.ExitCode);
        Assert.Equal(3, summary.Failed);
        Assert.Equal(3, _ledger.Rows.Count);
        Assert.Equal(2, _ledger.Runs.Single().ExitCode);
    }

    [Fact]
    public async Task Failure_counter_resets_after_success()
    {
        for (uint i = 1; i <= 5; i++) _source.Messages.Add(Msg(i, $"2010-01-0{i}"));
        _source.FetchFailure = uid => uid is 1 or 2 or 4 or 5 ? new IOException("boom") : null;

        var summary = await Workflow().RunAsync(Opts(), CancellationToken.None);

        Assert.False(summary.Aborted);
        Assert.Equal(4, summary.Failed);
        Assert.Equal(1, summary.Trashed);
    }

    [Fact]
    public async Task Auth_required_during_reconnect_propagates()
    {
        _source.Messages.Add(Msg(1, "2010-01-01"));
        _source.Messages.Add(Msg(2, "2010-01-02"));
        _source.FetchFailure = _ => new IOException("dropped");
        _source.ReconnectFailure = new AuthRequiredException("sign in again");

        await Assert.ThrowsAsync<AuthRequiredException>(() => Workflow().RunAsync(Opts(), CancellationToken.None));

        Assert.Empty(_source.Trashed);
        Assert.Equal(3, _ledger.Runs.Single().ExitCode);
    }

    [Fact]
    public async Task Max_messages_limits_the_run()
    {
        for (uint i = 1; i <= 5; i++) _source.Messages.Add(Msg(i, $"2010-01-0{i}"));
        var summary = await Workflow().RunAsync(Opts(max: 2), CancellationToken.None);
        Assert.Equal(2, summary.Selected);
        Assert.Equal(new uint[] { 1, 2 }, _source.Trashed);
    }

    [Fact]
    public async Task Run_row_records_counts()
    {
        _source.Messages.Add(Msg(1, "2010-01-01"));
        await Workflow().RunAsync(Opts(), CancellationToken.None);
        var run = _ledger.Runs.Single();
        Assert.Equal(RunMode.Move, run.Mode);
        Assert.Equal(Cutoff, run.CutoffDate);
        Assert.Equal(1, run.Selected);
        Assert.Equal(1, run.Trashed);
        Assert.Equal(0, run.ExitCode);
        Assert.NotNull(run.FinishedAt);
    }

    [Fact]
    public async Task Fallback_key_message_verifies_by_headers()
    {
        var m = Msg(1, "2010-01-01", messageId: null, from: "x@y.z", subject: "no id");
        _source.Messages.Add(m);
        _target.AppendTransform = t => t with { MessageId = null };

        var summary = await Workflow().RunAsync(Opts(), CancellationToken.None);

        Assert.Equal(1, summary.Trashed);
        Assert.Equal(KeyType.Fallback, Row(m).KeyType);
    }
}
```

- [ ] **Step 5: Run tests to verify they fail**

Run: `dotnet test --filter MoveWorkflowTests`
Expected: build errors for `MoveWorkflow`, `MoveOptions`, `RunSummary`.

- [ ] **Step 6: Implement RunSummary and MoveWorkflow**

`src/EmailArchive.Core/Workflows/RunSummary.cs`:

```csharp
using EmailArchive.Core.Selection;

namespace EmailArchive.Core.Workflows;

public sealed record MoveOptions(SelectionOptions Selection, bool Purge, int MaxConsecutiveFailures = 3);

public sealed record RunSummary(
    int Selected,
    int Copied,
    int AlreadyInTarget,
    int Verified,
    int Trashed,
    int Purged,
    int Failed,
    bool Aborted)
{
    public int ExitCode => Aborted ? 2 : Failed > 0 ? 1 : 0;

    public override string ToString() =>
        $"selected={Selected} copied={Copied} alreadyInTarget={AlreadyInTarget} verified={Verified} " +
        $"trashed={Trashed} purged={Purged} failed={Failed}{(Aborted ? " ABORTED" : "")}";
}
```

`src/EmailArchive.Core/Workflows/MoveWorkflow.cs`:

```csharp
using EmailArchive.Core.Auth;
using EmailArchive.Core.Ledger;
using EmailArchive.Core.Mail;
using EmailArchive.Core.Model;
using EmailArchive.Core.Selection;
using EmailArchive.Core.Verification;
using Microsoft.Extensions.Logging;

namespace EmailArchive.Core.Workflows;

public sealed class MoveWorkflow(IMailSource source, IMailTarget target, ILedger ledger, ILogger<MoveWorkflow> logger)
{
    public async Task<RunSummary> RunAsync(MoveOptions options, CancellationToken ct)
    {
        var run = await ledger.StartRunAsync(RunMode.Move, options.Selection.Cutoff, ct);
        var aborted = false;
        var exitCode = 2;

        try
        {
            await source.ConnectAsync(ct);
            await target.ConnectAsync(ct);
            await target.EnsureFoldersAsync(ct);

            var completed = await ledger.CompletedAsync(ct);
            var candidates = await source.ListOlderThanAsync(options.Selection.Cutoff, ct);
            var selected = MessageSelector.Select(candidates, options.Selection, completed);
            run.Selected = selected.Count;
            logger.LogInformation("Selected {Count} messages older than {Cutoff:yyyy-MM-dd}", selected.Count, options.Selection.Cutoff);

            var consecutiveFailures = 0;
            foreach (var item in selected)
            {
                ct.ThrowIfCancellationRequested();
                var ok = await ProcessAsync(item, run, options.Purge, ct);
                if (ok)
                {
                    consecutiveFailures = 0;
                }
                else if (++consecutiveFailures >= options.MaxConsecutiveFailures)
                {
                    logger.LogError("{Count} consecutive failures, aborting run", consecutiveFailures);
                    aborted = true;
                    break;
                }
            }

            exitCode = aborted ? 2 : run.Failed > 0 ? 1 : 0;
        }
        catch (AuthRequiredException)
        {
            exitCode = 3;
            throw;
        }
        finally
        {
            run.FinishedAt = DateTimeOffset.UtcNow;
            run.ExitCode = exitCode;
            await ledger.FinishRunAsync(run, CancellationToken.None);
        }

        return new RunSummary(run.Selected, run.Copied, run.AlreadyInTarget, run.Verified, run.Trashed, run.Purged, run.Failed, aborted);
    }

    private async Task<bool> ProcessAsync(SelectedMessage item, LedgerRun run, bool purge, CancellationToken ct)
    {
        var msg = item.Message;
        var row = await ledger.GetOrCreateAsync(item.Key, msg, run.Id, ct);
        row.LastRunId = run.Id;

        try
        {
            if (row.State == MessageState.Failed)
            {
                row.State = MessageState.Pending;
                row.FailureReason = null;
            }

            if (row.State is MessageState.Trashed or MessageState.Purged)
            {
                // Same key, different Gmail UID: a duplicate copy. The content is already in the
                // target, so re-verify against it and trash this copy as well.
                logger.LogInformation("UID {Uid} duplicates already-trashed key {Key}", msg.Uid, row.Key);
                row.State = MessageState.AlreadyInTarget;
                run.AlreadyInTarget++;
            }

            row.GmailUid = msg.Uid;

            if (row.State == MessageState.Pending)
            {
                if (row.TargetUid is not null)
                {
                    row.State = MessageState.AlreadyInTarget;
                    run.AlreadyInTarget++;
                }
                else
                {
                    var folder = FolderRouter.Route(msg);
                    var mime = await WithRetryAsync(() => source.FetchAsync(msg.Uid, ct), ct);
                    var uid = await WithRetryAsync(() => target.AppendAsync(folder, mime, msg.InternalDate, msg.IsSeen, msg.IsFlagged, ct), ct);
                    row.TargetFolder = folder;
                    row.TargetUid = uid;
                    row.CopiedAt = DateTimeOffset.UtcNow;
                    row.State = MessageState.Copied;
                    run.Copied++;
                }
                await ledger.SaveAsync(row, ct);
            }

            if (row.State is MessageState.AlreadyInTarget or MessageState.Copied)
            {
                var folder = row.TargetFolder ?? FolderRouter.Route(msg);
                var candidates = await WithRetryAsync(() => FindCandidatesAsync(folder, item, ct), ct);
                var result = Verifier.Verify(msg, candidates);
                if (!result.Success)
                    return await FailAsync(row, run, result.Reason!, ct);

                if (result.SizeWarning)
                    logger.LogWarning("UID {Uid}: size differs source={Source} target={Target}", msg.Uid, msg.Size, result.Match!.Size);

                row.TargetFolder = folder;
                row.TargetUid = result.Match!.Uid;
                row.TargetSize = result.Match.Size;
                row.VerifiedAt = DateTimeOffset.UtcNow;
                row.State = MessageState.Verified;
                run.Verified++;
                await ledger.SaveAsync(row, ct);
            }

            if (row.State == MessageState.Verified)
            {
                var trashUid = await WithRetryAsync(() => source.TrashAsync(msg.Uid, ct), ct);
                row.TrashUid = trashUid;
                row.TrashedAt = DateTimeOffset.UtcNow;
                row.State = MessageState.Trashed;
                run.Trashed++;
                await ledger.SaveAsync(row, ct);
            }

            if (row.State == MessageState.Trashed && purge && row.TrashUid is long trashUidValue)
            {
                await WithRetryVoidAsync(() => source.PurgeAsync((uint)trashUidValue, ct), ct);
                row.PurgedAt = DateTimeOffset.UtcNow;
                row.State = MessageState.Purged;
                run.Purged++;
                await ledger.SaveAsync(row, ct);
            }

            logger.LogInformation("UID {Uid} {Date:yyyy-MM-dd} \"{Subject}\" -> {State}", msg.Uid, msg.InternalDate, msg.Subject, row.State);
            return true;
        }
        catch (AuthRequiredException) { throw; }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return await FailAsync(row, run, ex.Message, ct);
        }
    }

    private Task<IReadOnlyList<TargetMessageInfo>> FindCandidatesAsync(TargetFolder folder, SelectedMessage item, CancellationToken ct) =>
        item.Key.Type == KeyType.MessageId
            ? target.FindByMessageIdAsync(folder, item.Key.Value, ct)
            : target.FindByFallbackAsync(folder, item.Message.HeaderDate, item.Message.From, item.Message.Subject, ct);

    /// <summary>Runs the operation; on a non-auth failure reconnects both sides and tries exactly once more.</summary>
    private async Task<T> WithRetryAsync<T>(Func<Task<T>> operation, CancellationToken ct)
    {
        try
        {
            return await operation();
        }
        catch (Exception ex) when (ex is not AuthRequiredException and not OperationCanceledException)
        {
            logger.LogWarning(ex, "Operation failed, reconnecting and retrying once");
            await source.ReconnectAsync(ct);
            await target.ReconnectAsync(ct);
            return await operation();
        }
    }

    private Task WithRetryVoidAsync(Func<Task> operation, CancellationToken ct) =>
        WithRetryAsync(async () => { await operation(); return true; }, ct);

    private async Task<bool> FailAsync(LedgerMessage row, LedgerRun run, string reason, CancellationToken ct)
    {
        logger.LogError("UID {Uid} failed: {Reason}", row.GmailUid, reason);
        // A purge failure after a successful trash keeps the Trashed state so the message is not re-processed.
        if (row.State != MessageState.Trashed)
            row.State = MessageState.Failed;
        row.FailureReason = reason;
        run.Failed++;
        await ledger.SaveAsync(row, CancellationToken.None);
        return false;
    }
}
```

- [ ] **Step 7: Run tests**

Run: `dotnet test`
Expected: all pass, including every MoveWorkflow test.

- [ ] **Step 8: Commit**

```powershell
git add .
git commit -m "feat: add mail interfaces, test fakes, and move workflow state machine"
```
(append the two attribution trailer lines)

---

### Task 8: Reconcile and report workflows

**Files:**
- Create: `src/EmailArchive.Core/Workflows/ReconcileWorkflow.cs`, `src/EmailArchive.Core/Workflows/ReportWorkflow.cs`
- Test: `tests/EmailArchive.Tests/Workflows/ReconcileWorkflowTests.cs`, `tests/EmailArchive.Tests/Workflows/ReportWorkflowTests.cs`

**Interfaces:**
- Consumes: `IMailTarget`, `IMailSource`, `ILedger`, `MessageSelector`, `MessageKeyBuilder`.
- Produces:
  - `record ReconcileSummary(int Scanned, int Recorded)`; `ReconcileWorkflow(IMailTarget, ILedger, ILogger<ReconcileWorkflow>)` with `RunAsync(ct)`. Its constructor takes no `IMailSource`, which is the guarantee that it never touches Gmail.
  - `record ReportSummary(int Count, long TotalBytes, DateTimeOffset? Oldest, DateTimeOffset? Newest, int AlreadyInTarget, int SentCount)`; `ReportWorkflow(IMailSource, ILedger, ILogger<ReportWorkflow>)` with `RunAsync(SelectionOptions, ct)`.

- [ ] **Step 1: Write failing tests**

`tests/EmailArchive.Tests/Workflows/ReconcileWorkflowTests.cs`:

```csharp
using EmailArchive.Core.Model;
using EmailArchive.Core.Workflows;
using EmailArchive.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using static EmailArchive.Tests.TestData;

namespace EmailArchive.Tests.Workflows;

public class ReconcileWorkflowTests
{
    private readonly FakeTarget _target = new();
    private readonly InMemoryLedger _ledger = new();

    private ReconcileWorkflow Workflow() => new(_target, _ledger, NullLogger<ReconcileWorkflow>.Instance);

    [Fact]
    public async Task Records_every_target_message_in_both_folders()
    {
        var a = Msg(1, "2010-01-01");
        var b = Msg(2, "2010-01-02", sent: true);
        _target.Folders[TargetFolder.Archive].Add(Target(a, uid: 10));
        _target.Folders[TargetFolder.Sent].Add(Target(b, TargetFolder.Sent, uid: 20));

        var summary = await Workflow().RunAsync(CancellationToken.None);

        Assert.Equal(2, summary.Scanned);
        Assert.Equal(2, summary.Recorded);
        var rowA = _ledger.Rows[MessageKeyBuilder.For(a).Value];
        Assert.Equal(MessageState.AlreadyInTarget, rowA.State);
        Assert.Equal(10, rowA.TargetUid);
        Assert.Equal(TargetFolder.Archive, rowA.TargetFolder);
        var rowB = _ledger.Rows[MessageKeyBuilder.For(b).Value];
        Assert.Equal(TargetFolder.Sent, rowB.TargetFolder);
        Assert.True(rowB.IsSent);
    }

    [Fact]
    public async Task Does_not_downgrade_trashed_rows()
    {
        var a = Msg(1, "2010-01-01");
        _target.Folders[TargetFolder.Archive].Add(Target(a, uid: 10));
        _ledger.Seed(a, MessageState.Trashed, r => r.TargetUid = 3);

        await Workflow().RunAsync(CancellationToken.None);

        var row = _ledger.Rows[MessageKeyBuilder.For(a).Value];
        Assert.Equal(MessageState.Trashed, row.State);
        Assert.Equal(3, row.TargetUid);
    }

    [Fact]
    public async Task Writes_a_reconcile_run_row()
    {
        await Workflow().RunAsync(CancellationToken.None);
        var run = _ledger.Runs.Single();
        Assert.Equal(RunMode.Reconcile, run.Mode);
        Assert.Null(run.CutoffDate);
        Assert.Equal(0, run.ExitCode);
    }
}
```

`tests/EmailArchive.Tests/Workflows/ReportWorkflowTests.cs`:

```csharp
using EmailArchive.Core.Model;
using EmailArchive.Core.Selection;
using EmailArchive.Core.Workflows;
using EmailArchive.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using static EmailArchive.Tests.TestData;

namespace EmailArchive.Tests.Workflows;

public class ReportWorkflowTests
{
    private static readonly DateTimeOffset Cutoff = new(2021, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private readonly FakeSource _source = new();
    private readonly InMemoryLedger _ledger = new();

    private ReportWorkflow Workflow() => new(_source, _ledger, NullLogger<ReportWorkflow>.Instance);

    [Fact]
    public async Task Summarizes_selection_without_touching_messages()
    {
        var a = Msg(1, "2010-01-01", size: 100);
        var b = Msg(2, "2012-06-01", size: 250, sent: true);
        var c = Msg(3, "2023-01-01", size: 999);
        _source.Messages.AddRange(new[] { a, b, c });
        _ledger.Seed(b, MessageState.Pending, r => r.TargetUid = 5);

        var summary = await Workflow().RunAsync(new SelectionOptions(Cutoff, null, null, false), CancellationToken.None);

        Assert.Equal(2, summary.Count);
        Assert.Equal(350, summary.TotalBytes);
        Assert.Equal(a.InternalDate, summary.Oldest);
        Assert.Equal(b.InternalDate, summary.Newest);
        Assert.Equal(1, summary.AlreadyInTarget);
        Assert.Equal(1, summary.SentCount);
        Assert.Empty(_source.Trashed);
        Assert.Single(_ledger.Rows); // only the seeded row, nothing created
        Assert.Equal(RunMode.Report, _ledger.Runs.Single().Mode);
    }

    [Fact]
    public async Task Empty_selection_reports_zero()
    {
        var summary = await Workflow().RunAsync(new SelectionOptions(Cutoff, null, null, false), CancellationToken.None);
        Assert.Equal(0, summary.Count);
        Assert.Null(summary.Oldest);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter "ReconcileWorkflowTests|ReportWorkflowTests"`
Expected: build errors.

- [ ] **Step 3: Implement**

`src/EmailArchive.Core/Workflows/ReconcileWorkflow.cs`:

```csharp
using EmailArchive.Core.Ledger;
using EmailArchive.Core.Mail;
using EmailArchive.Core.Model;
using Microsoft.Extensions.Logging;

namespace EmailArchive.Core.Workflows;

public sealed record ReconcileSummary(int Scanned, int Recorded);

/// <summary>Indexes what already exists in the target folders. Takes no IMailSource on purpose: it can never touch Gmail.</summary>
public sealed class ReconcileWorkflow(IMailTarget target, ILedger ledger, ILogger<ReconcileWorkflow> logger)
{
    public async Task<ReconcileSummary> RunAsync(CancellationToken ct)
    {
        var run = await ledger.StartRunAsync(RunMode.Reconcile, null, ct);
        int scanned = 0, recorded = 0;
        var exitCode = 2;

        try
        {
            await target.ConnectAsync(ct);
            await target.EnsureFoldersAsync(ct);

            foreach (var folder in new[] { TargetFolder.Archive, TargetFolder.Sent })
            {
                var messages = await target.ListAllAsync(folder, ct);
                logger.LogInformation("{Folder}: {Count} messages", folder, messages.Count);
                foreach (var message in messages)
                {
                    ct.ThrowIfCancellationRequested();
                    scanned++;
                    await ledger.RecordTargetAsync(MessageKeyBuilder.For(message), message, run.Id, ct);
                    recorded++;
                }
            }

            exitCode = 0;
        }
        finally
        {
            run.AlreadyInTarget = recorded;
            run.FinishedAt = DateTimeOffset.UtcNow;
            run.ExitCode = exitCode;
            await ledger.FinishRunAsync(run, CancellationToken.None);
        }

        return new ReconcileSummary(scanned, recorded);
    }
}
```

`src/EmailArchive.Core/Workflows/ReportWorkflow.cs`:

```csharp
using EmailArchive.Core.Ledger;
using EmailArchive.Core.Mail;
using EmailArchive.Core.Model;
using EmailArchive.Core.Selection;
using Microsoft.Extensions.Logging;

namespace EmailArchive.Core.Workflows;

public sealed record ReportSummary(int Count, long TotalBytes, DateTimeOffset? Oldest, DateTimeOffset? Newest, int AlreadyInTarget, int SentCount);

/// <summary>Dry run. Reads Gmail headers and the ledger, writes only a run row.</summary>
public sealed class ReportWorkflow(IMailSource source, ILedger ledger, ILogger<ReportWorkflow> logger)
{
    public async Task<ReportSummary> RunAsync(SelectionOptions options, CancellationToken ct)
    {
        var run = await ledger.StartRunAsync(RunMode.Report, options.Cutoff, ct);
        ReportSummary summary;
        var exitCode = 2;

        try
        {
            await source.ConnectAsync(ct);
            var completed = await ledger.CompletedAsync(ct);
            var candidates = await source.ListOlderThanAsync(options.Cutoff, ct);
            var selected = MessageSelector.Select(candidates, options, completed);

            var alreadyInTarget = 0;
            foreach (var item in selected)
            {
                var row = await ledger.FindAsync(item.Key, ct);
                if (row?.TargetUid is not null) alreadyInTarget++;
            }

            summary = new ReportSummary(
                selected.Count,
                selected.Sum(s => s.Message.Size),
                selected.Count == 0 ? null : selected[0].Message.InternalDate,
                selected.Count == 0 ? null : selected[^1].Message.InternalDate,
                alreadyInTarget,
                selected.Count(s => s.Message.IsSent));

            run.Selected = summary.Count;
            run.AlreadyInTarget = alreadyInTarget;
            logger.LogInformation("Report: {Summary}", summary);
            exitCode = 0;
        }
        finally
        {
            run.FinishedAt = DateTimeOffset.UtcNow;
            run.ExitCode = exitCode;
            await ledger.FinishRunAsync(run, CancellationToken.None);
        }

        return summary;
    }
}
```

- [ ] **Step 4: Run tests**

Run: `dotnet test`
Expected: all pass.

- [ ] **Step 5: Commit**

```powershell
git add .
git commit -m "feat: add reconcile and report workflows"
```
(append the two attribution trailer lines)

---
### Task 9: Credential store and Microsoft authentication

**Files:**
- Create: `src/EmailArchive.Core/Credentials/CredentialStore.cs`
- Create: `src/EmailArchive.Core/Auth/MicrosoftAuth.cs`

**Interfaces:**
- Consumes: `Microsoft365Settings` (Task 2), `AuthRequiredException` (Task 7).
- Produces:
  - `CredentialStore.ReadGmailAppPassword(string address)` → `string?`, `WriteGmailAppPassword(string address, string password)`, `ClearGmailAppPassword(string address)`.
  - `MicrosoftAuth(Microsoft365Settings)` with `AcquireTokenSilentAsync(ct)` and `AcquireTokenInteractiveAsync(ct)`, both returning `(string Username, string AccessToken)`. Silent throws `AuthRequiredException` when a browser sign-in is needed.

These two classes talk to Windows and to Microsoft, so they have no unit tests. They are exercised by the `login` and `credentials` commands in Task 12 and by the supervised first run in Task 14.

- [ ] **Step 1: Implement CredentialStore**

`src/EmailArchive.Core/Credentials/CredentialStore.cs`:

```csharp
using Meziantou.Framework.Win32;

namespace EmailArchive.Core.Credentials;

/// <summary>
/// Stores the Gmail app password in Windows Credential Manager (Control Panel > Credential Manager > Windows Credentials).
/// Entries are per Windows user, so a scheduled task running as the same user can read them.
/// </summary>
public static class CredentialStore
{
    private static string TargetName(string address) => $"EmailArchive:Gmail:{address}";

    public static string? ReadGmailAppPassword(string address) =>
        CredentialManager.ReadCredential(TargetName(address))?.Password;

    public static void WriteGmailAppPassword(string address, string password) =>
        CredentialManager.WriteCredential(TargetName(address), address, password, CredentialPersistence.LocalMachine);

    public static void ClearGmailAppPassword(string address)
    {
        if (CredentialManager.ReadCredential(TargetName(address)) is not null)
            CredentialManager.DeleteCredential(TargetName(address));
    }
}
```

- [ ] **Step 2: Implement MicrosoftAuth**

`src/EmailArchive.Core/Auth/MicrosoftAuth.cs`:

```csharp
using EmailArchive.Core.Config;
using Microsoft.Identity.Client;
using Microsoft.Identity.Client.Extensions.Msal;

namespace EmailArchive.Core.Auth;

/// <summary>
/// MSAL public client for delegated IMAP access. The token cache is encrypted with DPAPI in
/// %LOCALAPPDATA%\EmailArchive by the MSAL cache extension. Refresh tokens stay valid while used
/// at least every 90 days; a monthly run keeps them alive.
/// </summary>
public sealed class MicrosoftAuth(Microsoft365Settings settings)
{
    private static readonly string[] Scopes =
    [
        "email",
        "offline_access",
        "https://outlook.office.com/IMAP.AccessAsUser.All",
    ];

    private IPublicClientApplication? _app;

    private async Task<IPublicClientApplication> GetAppAsync()
    {
        if (_app is not null) return _app;

        var app = PublicClientApplicationBuilder
            .Create(settings.ClientId)
            .WithTenantId(settings.TenantId)
            .WithRedirectUri("http://localhost")
            .Build();

        var cacheDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EmailArchive");
        var storage = new StorageCreationPropertiesBuilder("msal_token_cache.bin", cacheDir).Build();
        var cacheHelper = await MsalCacheHelper.CreateAsync(storage);
        cacheHelper.RegisterCache(app.UserTokenCache);

        return _app = app;
    }

    public async Task<(string Username, string AccessToken)> AcquireTokenSilentAsync(CancellationToken ct)
    {
        var app = await GetAppAsync();
        var accounts = await app.GetAccountsAsync();
        var account = accounts.FirstOrDefault(a => string.Equals(a.Username, settings.Address, StringComparison.OrdinalIgnoreCase))
                      ?? accounts.FirstOrDefault();
        if (account is null)
            throw new AuthRequiredException("No cached Microsoft sign-in. Run: EmailArchive login");

        try
        {
            var result = await app.AcquireTokenSilent(Scopes, account).ExecuteAsync(ct);
            return (result.Account.Username, result.AccessToken);
        }
        catch (MsalUiRequiredException ex)
        {
            throw new AuthRequiredException("Microsoft sign-in has expired. Run: EmailArchive login", ex);
        }
    }

    public async Task<(string Username, string AccessToken)> AcquireTokenInteractiveAsync(CancellationToken ct)
    {
        var app = await GetAppAsync();
        var result = await app.AcquireTokenInteractive(Scopes)
            .WithLoginHint(settings.Address)
            .ExecuteAsync(ct);
        return (result.Account.Username, result.AccessToken);
    }
}
```

- [ ] **Step 3: Build**

Run: `dotnet build`
Expected: success. If `StorageCreationPropertiesBuilder` reports an obsolete three-argument constructor, the two-argument form above is the current one.

- [ ] **Step 4: Commit**

```powershell
git add .
git commit -m "feat: add Windows credential store and MSAL authentication"
```
(append the two attribution trailer lines)

---

### Task 10: Gmail IMAP source

**Files:**
- Create: `src/EmailArchive.Core/Mail/GmailSource.cs`

**Interfaces:**
- Consumes: `GmailSettings`, `IMailSource`, `SourceMessageInfo`, `GmailLabels`.
- Produces: `GmailSource(GmailSettings settings, string appPassword, ILogger<GmailSource> logger) : IMailSource`.

No unit tests: this is a thin MailKit wrapper. It is exercised by `report` in Task 14.

- [ ] **Step 1: Implement**

`src/EmailArchive.Core/Mail/GmailSource.cs`:

```csharp
using EmailArchive.Core.Config;
using EmailArchive.Core.Model;
using EmailArchive.Core.Selection;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using MailKit.Security;
using Microsoft.Extensions.Logging;
using MimeKit;

namespace EmailArchive.Core.Mail;

/// <summary>
/// Gmail over IMAP with an app password. Lists from [Gmail]/All Mail (which never contains Spam or Trash),
/// trashes by moving to [Gmail]/Trash, and purges by flagging Deleted inside Trash and expunging.
/// </summary>
public sealed class GmailSource(GmailSettings settings, string appPassword, ILogger<GmailSource> logger) : IMailSource
{
    private const int FetchBatchSize = 500;

    private ImapClient? _client;
    private IMailFolder? _allMail;
    private IMailFolder? _trash;

    public async Task ConnectAsync(CancellationToken ct)
    {
        var client = new ImapClient();
        await client.ConnectAsync(settings.ImapHost, settings.ImapPort, SecureSocketOptions.SslOnConnect, ct);
        await client.AuthenticateAsync(settings.Address, appPassword, ct);

        if (!client.Capabilities.HasFlag(ImapCapabilities.GMailExt1))
            throw new InvalidOperationException("The IMAP server did not advertise Gmail extensions. Check Gmail:ImapHost.");

        _allMail = client.GetFolder(SpecialFolder.All)
            ?? throw new InvalidOperationException("Gmail did not expose the All Mail folder. Enable 'Show in IMAP' for All Mail in Gmail label settings.");
        _trash = client.GetFolder(SpecialFolder.Trash)
            ?? throw new InvalidOperationException("Gmail did not expose the Trash folder. Enable 'Show in IMAP' for Trash in Gmail label settings.");

        await _allMail.OpenAsync(FolderAccess.ReadWrite, ct);
        _client = client;
        logger.LogInformation("Connected to Gmail as {Address}", settings.Address);
    }

    public async Task ReconnectAsync(CancellationToken ct)
    {
        await DisposeAsync();
        await ConnectAsync(ct);
    }

    public async Task<IReadOnlyList<SourceMessageInfo>> ListOlderThanAsync(DateTimeOffset cutoff, CancellationToken ct)
    {
        var folder = AllMail;
        var uids = await folder.SearchAsync(SearchQuery.DeliveredBefore(cutoff.UtcDateTime), ct);
        logger.LogInformation("Gmail All Mail: {Count} messages delivered before {Cutoff:yyyy-MM-dd}", uids.Count, cutoff);

        const MessageSummaryItems items =
            MessageSummaryItems.UniqueId | MessageSummaryItems.InternalDate | MessageSummaryItems.Envelope |
            MessageSummaryItems.Size | MessageSummaryItems.Flags | MessageSummaryItems.GMailLabels;

        var result = new List<SourceMessageInfo>(uids.Count);
        foreach (var batch in uids.Chunk(FetchBatchSize))
        {
            var summaries = await folder.FetchAsync(batch, items, ct);
            result.AddRange(summaries.Select(ToInfo));
        }
        return result;
    }

    public Task<MimeMessage> FetchAsync(uint uid, CancellationToken ct) =>
        AllMail.GetMessageAsync(new UniqueId(uid), ct);

    public async Task<uint?> TrashAsync(uint uid, CancellationToken ct)
    {
        var moved = await AllMail.MoveToAsync(new UniqueId(uid), Trash, ct);
        return moved?.Id;
    }

    public async Task PurgeAsync(uint trashUid, CancellationToken ct)
    {
        var trash = Trash;
        await trash.OpenAsync(FolderAccess.ReadWrite, ct);
        try
        {
            var uid = new UniqueId(trashUid);
            await trash.AddFlagsAsync(uid, MessageFlags.Deleted, true, ct);
            await trash.ExpungeAsync(new[] { uid }, ct);
        }
        finally
        {
            await AllMail.OpenAsync(FolderAccess.ReadWrite, ct);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_client is null) return;
        try
        {
            if (_client.IsConnected) await _client.DisconnectAsync(true);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Ignoring error while disconnecting from Gmail");
        }
        _client.Dispose();
        _client = null;
        _allMail = null;
        _trash = null;
    }

    private IMailFolder AllMail => _allMail ?? throw new InvalidOperationException("Not connected to Gmail");
    private IMailFolder Trash => _trash ?? throw new InvalidOperationException("Not connected to Gmail");

    private static SourceMessageInfo ToInfo(IMessageSummary s)
    {
        var labels = s.GMailLabels ?? Array.Empty<string>();
        var envelope = s.Envelope;
        var flags = s.Flags ?? MessageFlags.None;
        return new SourceMessageInfo(
            Uid: s.UniqueId.Id,
            MessageId: envelope?.MessageId,
            InternalDate: s.InternalDate ?? envelope?.Date ?? DateTimeOffset.MinValue,
            HeaderDate: envelope?.Date,
            From: envelope?.From?.Mailboxes.FirstOrDefault()?.Address ?? "",
            Subject: envelope?.Subject ?? "",
            Size: s.Size ?? 0,
            IsSent: GmailLabels.IsSent(labels),
            IsChat: GmailLabels.IsChat(labels),
            IsSeen: flags.HasFlag(MessageFlags.Seen),
            IsFlagged: flags.HasFlag(MessageFlags.Flagged));
    }
}
```

- [ ] **Step 2: Build**

Run: `dotnet build`
Expected: success.

- [ ] **Step 3: Commit**

```powershell
git add .
git commit -m "feat: add Gmail IMAP source"
```
(append the two attribution trailer lines)

---

### Task 11: Microsoft 365 IMAP target

**Files:**
- Create: `src/EmailArchive.Core/Mail/M365Target.cs`

**Interfaces:**
- Consumes: `Microsoft365Settings`, `MicrosoftAuth`, `IMailTarget`, `TargetMessageInfo`.
- Produces: `M365Target(Microsoft365Settings settings, MicrosoftAuth auth, ILogger<M365Target> logger, TimeSpan? searchRetryDelay = null) : IMailTarget`.

No unit tests: thin MailKit wrapper, exercised by `reconcile` in Task 14.

- [ ] **Step 1: Implement**

`src/EmailArchive.Core/Mail/M365Target.cs`:

```csharp
using EmailArchive.Core.Auth;
using EmailArchive.Core.Config;
using EmailArchive.Core.Model;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using MailKit.Security;
using Microsoft.Extensions.Logging;
using MimeKit;

namespace EmailArchive.Core.Mail;

/// <summary>
/// Exchange Online over IMAP with OAuth2 (SASL XOAUTH2). Folder paths in settings use '/' and are created on demand
/// under the personal namespace. Exchange indexes appended mail asynchronously, so header searches retry briefly.
/// </summary>
public sealed class M365Target(
    Microsoft365Settings settings,
    MicrosoftAuth auth,
    ILogger<M365Target> logger,
    TimeSpan? searchRetryDelay = null) : IMailTarget
{
    private const int FetchBatchSize = 500;
    private const int SearchAttempts = 3;
    private readonly TimeSpan _searchRetryDelay = searchRetryDelay ?? TimeSpan.FromSeconds(2);

    private ImapClient? _client;
    private IMailFolder? _archive;
    private IMailFolder? _sent;

    public async Task ConnectAsync(CancellationToken ct)
    {
        var (username, token) = await auth.AcquireTokenSilentAsync(ct);
        var client = new ImapClient();
        await client.ConnectAsync(settings.ImapHost, settings.ImapPort, SecureSocketOptions.SslOnConnect, ct);
        await client.AuthenticateAsync(new SaslMechanismOAuth2(username, token), ct);
        _client = client;
        logger.LogInformation("Connected to Microsoft 365 as {Username}", username);
    }

    public async Task ReconnectAsync(CancellationToken ct)
    {
        await DisposeAsync();
        await ConnectAsync(ct);
        await EnsureFoldersAsync(ct);
    }

    public async Task EnsureFoldersAsync(CancellationToken ct)
    {
        _archive = await EnsurePathAsync(settings.ArchiveFolder, ct);
        _sent = await EnsurePathAsync(settings.SentFolder, ct);
    }

    public async Task<uint?> AppendAsync(TargetFolder folder, MimeMessage message, DateTimeOffset internalDate, bool seen, bool flagged, CancellationToken ct)
    {
        var flags = (seen ? MessageFlags.Seen : MessageFlags.None) | (flagged ? MessageFlags.Flagged : MessageFlags.None);
        var uid = await Resolve(folder).AppendAsync(message, flags, internalDate, ct);
        return uid?.Id;
    }

    public async Task<IReadOnlyList<TargetMessageInfo>> FindByMessageIdAsync(TargetFolder folder, string messageId, CancellationToken ct)
    {
        var mailFolder = Resolve(folder);
        await mailFolder.OpenAsync(FolderAccess.ReadOnly, ct);

        for (var attempt = 1; ; attempt++)
        {
            var uids = await mailFolder.SearchAsync(SearchQuery.HeaderContains("Message-Id", messageId), ct);
            if (uids.Count > 0 || attempt >= SearchAttempts)
                return await SummarizeAsync(mailFolder, folder, uids, ct);

            logger.LogDebug("Message-Id {MessageId} not yet searchable in {Folder}, retrying", messageId, folder);
            await Task.Delay(_searchRetryDelay, ct);
        }
    }

    public async Task<IReadOnlyList<TargetMessageInfo>> FindByFallbackAsync(TargetFolder folder, DateTimeOffset? headerDate, string from, string subject, CancellationToken ct)
    {
        var mailFolder = Resolve(folder);
        await mailFolder.OpenAsync(FolderAccess.ReadOnly, ct);

        SearchQuery query = SearchQuery.All;
        if (headerDate is DateTimeOffset date)
            query = query.And(SearchQuery.SentSince(date.UtcDateTime.AddDays(-1))).And(SearchQuery.SentBefore(date.UtcDateTime.AddDays(2)));
        if (!string.IsNullOrWhiteSpace(subject))
            query = query.And(SearchQuery.SubjectContains(subject));
        if (!string.IsNullOrWhiteSpace(from))
            query = query.And(SearchQuery.FromContains(HeaderNormalizer.NormalizeAddress(from)));

        var uids = await mailFolder.SearchAsync(query, ct);
        return await SummarizeAsync(mailFolder, folder, uids, ct);
    }

    public async Task<IReadOnlyList<TargetMessageInfo>> ListAllAsync(TargetFolder folder, CancellationToken ct)
    {
        var mailFolder = Resolve(folder);
        await mailFolder.OpenAsync(FolderAccess.ReadOnly, ct);
        var uids = await mailFolder.SearchAsync(SearchQuery.All, ct);
        return await SummarizeAsync(mailFolder, folder, uids, ct);
    }

    public async ValueTask DisposeAsync()
    {
        if (_client is null) return;
        try
        {
            if (_client.IsConnected) await _client.DisconnectAsync(true);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Ignoring error while disconnecting from Microsoft 365");
        }
        _client.Dispose();
        _client = null;
        _archive = null;
        _sent = null;
    }

    private async Task<IMailFolder> EnsurePathAsync(string path, CancellationToken ct)
    {
        var client = _client ?? throw new InvalidOperationException("Not connected to Microsoft 365");
        var folder = client.GetFolder(client.PersonalNamespaces[0]);

        foreach (var segment in path.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            IMailFolder? next = null;
            try
            {
                next = await folder.GetSubfolderAsync(segment, ct);
            }
            catch (FolderNotFoundException)
            {
                logger.LogInformation("Creating folder {Segment} under {Parent}", segment, folder.FullName);
            }
            folder = next ?? await folder.CreateAsync(segment, true, ct);
        }
        return folder;
    }

    private IMailFolder Resolve(TargetFolder folder) => folder switch
    {
        TargetFolder.Archive => _archive ?? throw new InvalidOperationException("Folders not ensured"),
        TargetFolder.Sent => _sent ?? throw new InvalidOperationException("Folders not ensured"),
        _ => throw new ArgumentOutOfRangeException(nameof(folder)),
    };

    private static async Task<IReadOnlyList<TargetMessageInfo>> SummarizeAsync(IMailFolder mailFolder, TargetFolder folder, IList<UniqueId> uids, CancellationToken ct)
    {
        const MessageSummaryItems items = MessageSummaryItems.UniqueId | MessageSummaryItems.Envelope | MessageSummaryItems.Size;
        var result = new List<TargetMessageInfo>(uids.Count);
        foreach (var batch in uids.Chunk(FetchBatchSize))
        {
            var summaries = await mailFolder.FetchAsync(batch, items, ct);
            result.AddRange(summaries.Select(s => new TargetMessageInfo(
                folder,
                s.UniqueId.Id,
                s.Envelope?.MessageId,
                s.Envelope?.Date,
                s.Envelope?.From?.Mailboxes.FirstOrDefault()?.Address ?? "",
                s.Envelope?.Subject ?? "",
                s.Size ?? 0)));
        }
        return result;
    }
}
```

- [ ] **Step 2: Build**

Run: `dotnet build`
Expected: success.

- [ ] **Step 3: Commit**

```powershell
git add .
git commit -m "feat: add Microsoft 365 IMAP target with OAuth2"
```
(append the two attribution trailer lines)

---
### Task 12: Command-line application

**Files:**
- Create: `src/EmailArchive.Cli/AppHost.cs`, `src/EmailArchive.Cli/Commands.cs`
- Replace: `src/EmailArchive.Cli/Program.cs`

**Interfaces:**
- Consumes: everything in Core.
- Produces: the `EmailArchive` executable with commands `report`, `reconcile`, `move`, `login`, `credentials set-gmail`, `credentials clear`.

The CLI has no unit tests. Each command is verified by running it in Step 4.

- [ ] **Step 1: Write AppHost**

`src/EmailArchive.Cli/AppHost.cs`:

```csharp
using EmailArchive.Core.Auth;
using EmailArchive.Core.Config;
using EmailArchive.Core.Credentials;
using EmailArchive.Core.Ledger;
using EmailArchive.Core.Mail;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Extensions.Logging;

namespace EmailArchive.Cli;

/// <summary>Loads configuration, sets up logging, builds the pieces a command needs, and maps exceptions to exit codes.</summary>
public sealed class AppHost : IAsyncDisposable
{
    public AppSettings Settings { get; }
    public ILoggerFactory LoggerFactory { get; }
    public MicrosoftAuth Auth { get; }

    private EmailArchiveDbContext? _db;

    private AppHost(AppSettings settings, ILoggerFactory loggerFactory)
    {
        Settings = settings;
        LoggerFactory = loggerFactory;
        Auth = new MicrosoftAuth(settings.Microsoft365);
    }

    public static AppHost Create(bool validate)
    {
        var configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false)
            .AddJsonFile("appsettings.local.json", optional: true)
            .Build();

        var settings = configuration.Get<AppSettings>() ?? new AppSettings();
        if (validate) settings.Validate();

        var logDir = Path.IsPathRooted(settings.Logging.Directory)
            ? settings.Logging.Directory
            : Path.Combine(AppContext.BaseDirectory, settings.Logging.Directory);
        Directory.CreateDirectory(logDir);
        var logFile = Path.Combine(logDir, $"run-{DateTime.Now:yyyyMMdd-HHmmss}.log");

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.Console(outputTemplate: "{Timestamp:HH:mm:ss} {Level:u3} {Message:lj}{NewLine}{Exception}")
            .WriteTo.File(logFile, outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} {Level:u3} {SourceContext} {Message:lj}{NewLine}{Exception}")
            .CreateLogger();

        return new AppHost(settings, new SerilogLoggerFactory(Log.Logger));
    }

    /// <summary>Runs a command with exit-code mapping. Pass validate=false for commands that only need settings.</summary>
    public static async Task<int> RunAsync(bool validate, Func<AppHost, CancellationToken, Task<int>> command, CancellationToken ct)
    {
        try
        {
            await using var host = Create(validate);
            try
            {
                return await command(host, ct);
            }
            catch (AuthRequiredException ex)
            {
                Log.Error(ex.Message);
                return 3;
            }
            catch (ConfigurationException ex)
            {
                Log.Error(ex.Message);
                return 2;
            }
            catch (OperationCanceledException)
            {
                Log.Warning("Cancelled");
                return 2;
            }
            catch (Exception ex)
            {
                Log.Fatal(ex, "Run aborted");
                return 2;
            }
        }
        catch (ConfigurationException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 2;
        }
        finally
        {
            await Log.CloseAndFlushAsync();
        }
    }

    public async Task<ILedger> CreateLedgerAsync(CancellationToken ct)
    {
        var options = new DbContextOptionsBuilder<EmailArchiveDbContext>()
            .UseSqlServer(Settings.Ledger.ConnectionString)
            .Options;
        _db = new EmailArchiveDbContext(options);
        await _db.Database.MigrateAsync(ct);
        return new SqlLedger(_db);
    }

    public IMailSource CreateSource()
    {
        var password = CredentialStore.ReadGmailAppPassword(Settings.Gmail.Address)
            ?? throw new ConfigurationException(
                $"No Gmail app password stored for {Settings.Gmail.Address}. Run: EmailArchive credentials set-gmail");
        return new GmailSource(Settings.Gmail, password, LoggerFactory.CreateLogger<GmailSource>());
    }

    public IMailTarget CreateTarget() =>
        new M365Target(Settings.Microsoft365, Auth, LoggerFactory.CreateLogger<M365Target>());

    public async ValueTask DisposeAsync()
    {
        if (_db is not null) await _db.DisposeAsync();
    }
}
```

- [ ] **Step 2: Write Commands**

`src/EmailArchive.Cli/Commands.cs`:

```csharp
using EmailArchive.Core.Credentials;
using EmailArchive.Core.Selection;
using EmailArchive.Core.Workflows;
using Microsoft.Extensions.Logging;

namespace EmailArchive.Cli;

public static class Commands
{
    public static async Task<int> ReportAsync(AppHost host, int? keepYears, int? maxMessages, long? maxBytes, CancellationToken ct)
    {
        var options = Selection(host, keepYears, maxMessages, maxBytes);
        var ledger = await host.CreateLedgerAsync(ct);
        await using var source = host.CreateSource();

        var workflow = new ReportWorkflow(source, ledger, host.LoggerFactory.CreateLogger<ReportWorkflow>());
        var summary = await workflow.RunAsync(options, ct);

        Console.WriteLine();
        Console.WriteLine($"Cutoff:            {options.Cutoff:yyyy-MM-dd} (keep {keepYears ?? host.Settings.Selection.KeepYears} years)");
        Console.WriteLine($"Would move:        {summary.Count:N0} messages, {summary.TotalBytes / 1_048_576.0:N1} MB");
        Console.WriteLine($"Of which sent:     {summary.SentCount:N0}");
        Console.WriteLine($"Already in target: {summary.AlreadyInTarget:N0} (per ledger; run reconcile to refresh)");
        Console.WriteLine($"Date range:        {summary.Oldest:yyyy-MM-dd} to {summary.Newest:yyyy-MM-dd}");
        Console.WriteLine("Nothing was changed.");
        return 0;
    }

    public static async Task<int> ReconcileAsync(AppHost host, CancellationToken ct)
    {
        var ledger = await host.CreateLedgerAsync(ct);
        await using var target = host.CreateTarget();

        var workflow = new ReconcileWorkflow(target, ledger, host.LoggerFactory.CreateLogger<ReconcileWorkflow>());
        var summary = await workflow.RunAsync(ct);

        Console.WriteLine();
        Console.WriteLine($"Scanned {summary.Scanned:N0} messages in the target folders, recorded {summary.Recorded:N0} in the ledger.");
        return 0;
    }

    public static async Task<int> MoveAsync(AppHost host, int? keepYears, int? maxMessages, long? maxBytes, bool purge, CancellationToken ct)
    {
        var options = new MoveOptions(Selection(host, keepYears, maxMessages, maxBytes), purge);
        var ledger = await host.CreateLedgerAsync(ct);
        await using var source = host.CreateSource();
        await using var target = host.CreateTarget();

        var workflow = new MoveWorkflow(source, target, ledger, host.LoggerFactory.CreateLogger<MoveWorkflow>());
        var summary = await workflow.RunAsync(options, ct);

        Console.WriteLine();
        Console.WriteLine($"Result: {summary}");
        if (!purge && summary.Trashed > 0)
            Console.WriteLine("Trashed messages stay in Gmail Trash for 30 days (and still count toward storage) unless you empty Trash or rerun with --purge.");
        return summary.ExitCode;
    }

    public static async Task<int> LoginAsync(AppHost host, CancellationToken ct)
    {
        Console.WriteLine("A browser window will open for Microsoft sign-in...");
        var (username, _) = await host.Auth.AcquireTokenInteractiveAsync(ct);
        Console.WriteLine($"Signed in as {username}. The token is cached for future runs.");
        return 0;
    }

    public static Task<int> SetGmailCredentialAsync(AppHost host)
    {
        var address = host.Settings.Gmail.Address;
        Console.Write($"Enter the Gmail app password for {address} (input hidden): ");
        var password = ReadHidden();
        if (string.IsNullOrWhiteSpace(password))
        {
            Console.Error.WriteLine("No password entered.");
            return Task.FromResult(2);
        }
        CredentialStore.WriteGmailAppPassword(address, password.Replace(" ", ""));
        Console.WriteLine("Stored in Windows Credential Manager.");
        return Task.FromResult(0);
    }

    public static Task<int> ClearCredentialAsync(AppHost host)
    {
        CredentialStore.ClearGmailAppPassword(host.Settings.Gmail.Address);
        Console.WriteLine("Gmail app password removed from Windows Credential Manager.");
        Console.WriteLine("To forget the Microsoft sign-in, delete %LOCALAPPDATA%\\EmailArchive\\msal_token_cache.bin.");
        return Task.FromResult(0);
    }

    private static SelectionOptions Selection(AppHost host, int? keepYears, int? maxMessages, long? maxBytes)
    {
        var years = keepYears ?? host.Settings.Selection.KeepYears;
        var cutoff = CutoffCalculator.Compute(DateTimeOffset.UtcNow, years);
        return new SelectionOptions(cutoff, maxMessages, maxBytes, host.Settings.Selection.ExcludeChats);
    }

    private static string ReadHidden()
    {
        var chars = new System.Text.StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter) break;
            if (key.Key == ConsoleKey.Backspace)
            {
                if (chars.Length > 0) chars.Length--;
                continue;
            }
            if (!char.IsControl(key.KeyChar)) chars.Append(key.KeyChar);
        }
        Console.WriteLine();
        return chars.ToString();
    }
}
```

- [ ] **Step 3: Write Program.cs**

`src/EmailArchive.Cli/Program.cs`:

```csharp
using System.CommandLine;
using EmailArchive.Cli;

var keepYears = new Option<int?>("--keep-years") { Description = "Keep this many years in Gmail (overrides Selection:KeepYears)" };
var maxMessages = new Option<int?>("--max-messages") { Description = "Stop after this many messages" };
var maxBytes = new Option<long?>("--max-bytes") { Description = "Stop before exceeding this many bytes" };
var purge = new Option<bool>("--purge") { Description = "Expunge trashed messages from Gmail immediately (default: leave in Trash)" };

var report = new Command("report", "Dry run: show what would move. Changes nothing.") { keepYears, maxMessages, maxBytes };
report.SetAction((parse, ct) => AppHost.RunAsync(validate: true,
    (host, token) => Commands.ReportAsync(host, parse.GetValue(keepYears), parse.GetValue(maxMessages), parse.GetValue(maxBytes), token), ct));

var reconcile = new Command("reconcile", "Index messages already in the Microsoft 365 target folders. Never touches Gmail.");
reconcile.SetAction((parse, ct) => AppHost.RunAsync(validate: true, Commands.ReconcileAsync, ct));

var move = new Command("move", "Copy, verify, then trash the oldest Gmail messages.") { keepYears, maxMessages, maxBytes, purge };
move.SetAction((parse, ct) => AppHost.RunAsync(validate: true,
    (host, token) => Commands.MoveAsync(host, parse.GetValue(keepYears), parse.GetValue(maxMessages), parse.GetValue(maxBytes), parse.GetValue(purge), token), ct));

var login = new Command("login", "Sign in to Microsoft interactively and cache the token.");
login.SetAction((parse, ct) => AppHost.RunAsync(validate: true, Commands.LoginAsync, ct));

var setGmail = new Command("set-gmail", "Store the Gmail app password in Windows Credential Manager.");
setGmail.SetAction((parse, ct) => AppHost.RunAsync(validate: false, (host, _) => Commands.SetGmailCredentialAsync(host), ct));

var clear = new Command("clear", "Remove the stored Gmail app password.");
clear.SetAction((parse, ct) => AppHost.RunAsync(validate: false, (host, _) => Commands.ClearCredentialAsync(host), ct));

var credentials = new Command("credentials", "Manage stored secrets.") { setGmail, clear };

var root = new RootCommand("Move the oldest Gmail messages into a Microsoft 365 mailbox, verify, then trash them in Gmail.")
{
    report, reconcile, move, login, credentials,
};

return await root.Parse(args).InvokeAsync();
```

Set the assembly name so the executable is `EmailArchive.exe`. In `src/EmailArchive.Cli/EmailArchive.Cli.csproj` add inside the first `<PropertyGroup>`:

```xml
<AssemblyName>EmailArchive</AssemblyName>
<RootNamespace>EmailArchive.Cli</RootNamespace>
```

- [ ] **Step 4: Build and verify command behavior without real accounts**

Run: `dotnet build`
Expected: success.

Run: `dotnet run --project src/EmailArchive.Cli -- --help`
Expected: the five commands listed with descriptions.

Run: `dotnet run --project src/EmailArchive.Cli -- report`
Expected: exit code 2 and a message listing the placeholder configuration problems (no `appsettings.local.json` yet). Check with `echo $LASTEXITCODE`.

Run: `dotnet run --project src/EmailArchive.Cli -- credentials clear`
Expected: exit code 0 (no validation needed), prints the removal message.

- [ ] **Step 5: Commit**

```powershell
git add .
git commit -m "feat: add command-line application with report, reconcile, move, login, and credentials"
```
(append the two attribution trailer lines)

---

### Task 13: Setup documentation

**Files:**
- Create: `docs/setup-gmail.md`, `docs/setup-entra.md`, `docs/setup-schedule.md`
- Modify: `README.md` (replace the "Status: in design" paragraph and add usage)

- [ ] **Step 1: Write the Gmail setup guide**

`docs/setup-gmail.md`:

```markdown
# Gmail setup

EmailArchive signs in to Gmail over IMAP with an app password. App passwords require 2-Step Verification.

1. Open https://myaccount.google.com/security and turn on **2-Step Verification** if it is off.
2. Open https://myaccount.google.com/apppasswords.
3. Enter the name `EmailArchive` and click **Create**.
4. Copy the 16-character password shown. Google shows it only once.
5. In a terminal, from the folder containing `EmailArchive.exe`, run:

       EmailArchive credentials set-gmail

   Paste the password when prompted. Spaces are removed automatically. It is stored in
   Windows Credential Manager under `EmailArchive:Gmail:<your address>`, not in any file.

6. Confirm IMAP access: in Gmail, open **Settings > See all settings > Forwarding and POP/IMAP**.
   On current Gmail accounts IMAP is always on. If you see an "Enable IMAP" option, enable it.
7. Under **Settings > Labels**, confirm **All Mail** and **Trash** have **Show in IMAP** checked.

The "Auto-Expunge" and "When a message is marked as deleted" options on the IMAP settings page do not
affect EmailArchive: it moves messages to Trash explicitly rather than relying on the delete flag.

To revoke access later, delete the app password at https://myaccount.google.com/apppasswords and run
`EmailArchive credentials clear`.
```

- [ ] **Step 2: Write the Entra setup guide**

`docs/setup-entra.md`:

```markdown
# Microsoft Entra app registration

Exchange Online no longer accepts passwords over IMAP, so EmailArchive signs in with OAuth2 through an
app registration in your own tenant. You need to be a global administrator (or application administrator)
of the Microsoft 365 tenant that owns the destination mailbox. No one outside your tenant is involved.

## Register the application

1. Open https://entra.microsoft.com and sign in as the tenant administrator.
2. Go to **Identity > Applications > App registrations** and click **New registration**.
3. Name: `EmailArchive`.
4. Supported account types: **Accounts in this organizational directory only**.
5. Redirect URI: choose platform **Public client/native (mobile & desktop)** and enter `http://localhost`.
6. Click **Register**.
7. On the **Overview** page, copy the **Application (client) ID** and the **Directory (tenant) ID**.

## Allow the desktop sign-in flow

1. In the registration, open **Authentication**.
2. Under **Advanced settings**, set **Allow public client flows** to **Yes**.
3. Click **Save**.

## Grant IMAP permission

1. Open **API permissions** and click **Add a permission**.
2. Choose the **APIs my organization uses** tab and search for `Office 365 Exchange Online`.
3. Select it, then choose **Delegated permissions**.
4. Tick **IMAP.AccessAsUser.All** and click **Add permissions**.
5. Click **Grant admin consent for <your tenant>** and confirm.

The permission list should now show `IMAP.AccessAsUser.All` with a green "Granted" status, alongside
the default `User.Read`.

## Configure EmailArchive

Create `appsettings.local.json` next to `appsettings.json` (this file is git-ignored):

    {
      "Gmail": { "Address": "you@gmail.com" },
      "Microsoft365": {
        "Address": "you@yourdomain.com",
        "TenantId": "<Directory (tenant) ID>",
        "ClientId": "<Application (client) ID>"
      }
    }

Replace the two addresses with your real ones. Then run:

    EmailArchive login

A browser opens for Microsoft sign-in. Sign in as the destination mailbox owner and accept the consent
prompt. The token is cached encrypted in `%LOCALAPPDATA%\EmailArchive\msal_token_cache.bin` and renews
itself as long as EmailArchive runs at least every 90 days.
```

- [ ] **Step 3: Write the scheduling guide**

`docs/setup-schedule.md`:

```markdown
# Running monthly with Task Scheduler

## Publish the app to a fixed folder

From the repository root:

    dotnet publish src/EmailArchive.Cli -c Release -o C:\Tools\EmailArchive

Copy your `appsettings.local.json` into `C:\Tools\EmailArchive` as well. Logs go to
`C:\Tools\EmailArchive\logs`.

## Create the task

Run in an elevated PowerShell window, after a successful manual `move` run:

    schtasks /Create /TN "EmailArchive Monthly" /SC MONTHLY /D 1 /ST 03:00 /TR "\"C:\Tools\EmailArchive\EmailArchive.exe\" move --purge" /RL LIMITED /F

This runs on the first of every month at 03:00 as the current user, with "Run only when user is logged
on" semantics. The Gmail app password in Credential Manager and the Microsoft token cache both belong
to that user, so no secrets need to be entered into the task.

If you prefer the task to run while logged off, open Task Scheduler, edit the task, choose
**Run whether user is logged on or not**, and enter your Windows password. Credential Manager and the
token cache remain readable because the task still runs as you.

## Checking results

- Each run writes `logs\run-<timestamp>.log`.
- The `Runs` table in the `EmailArchive` database has one row per run with counts and the exit code.
- Exit code 3 means the Microsoft sign-in expired. Run `EmailArchive login` once by hand.

To remove the task:

    schtasks /Delete /TN "EmailArchive Monthly" /F
```

- [ ] **Step 4: Update the README**

Replace the paragraph beginning `**Status: in design.` in `README.md` with:

```markdown
**Status: working.** See the setup guides before the first run.

## Setup

1. [Gmail app password](docs/setup-gmail.md)
2. [Microsoft Entra app registration](docs/setup-entra.md) and `appsettings.local.json`
3. A local SQL Server instance. The `Ledger:ConnectionString` default points at `localhost` with
   Windows authentication and database `EmailArchive`, created automatically on first run.

## Usage

    EmailArchive report                      # dry run: what would move
    EmailArchive reconcile                   # index what is already in the target folders
    EmailArchive move --max-messages 20      # first supervised batch
    EmailArchive move                        # full backlog, messages go to Gmail Trash
    EmailArchive move --purge                # same, and expunge from Trash immediately
    EmailArchive login                       # redo the Microsoft sign-in (exit code 3 asks for this)

Options for `report` and `move`: `--keep-years N`, `--max-messages N`, `--max-bytes N`.

Exit codes: 0 success, 1 some messages failed (see the log), 2 aborted or configuration error,
3 Microsoft sign-in required.

Schedule it monthly with [Task Scheduler](docs/setup-schedule.md).
```

Also replace the `## License` section body with `MIT, see [LICENSE](LICENSE).`

- [ ] **Step 5: Scan for personal information and commit**

Run: `git grep -niE "@gmail\.com|@[a-z0-9-]+\.(com|org|net)" -- ':!docs/setup-*.md' ':!README.md' ':!tests'`
Expected: no matches outside placeholders. Then inspect any hit in docs and README: only `you@gmail.com`, `you@yourdomain.com`, `example.com`, and `noreply@anthropic.com` in commit trailers are allowed.

```powershell
git add .
git commit -m "docs: add Gmail, Entra, and Task Scheduler setup guides"
```
(append the two attribution trailer lines)

---

### Task 14: Supervised first run

This task is performed by the owner on the real accounts. Nothing here is committed except a note
in the README if a step reveals a needed change. Stop at the first unexpected result and fix it
before continuing.

- [ ] **Step 1: One-time setup**

Follow `docs/setup-gmail.md` and `docs/setup-entra.md`. Confirm:
- `EmailArchive credentials set-gmail` reports stored.
- `EmailArchive login` opens the browser and reports the signed-in address.

- [ ] **Step 2: Dry run**

Run: `EmailArchive report`
Expected: connects to Gmail, prints the count, size, and date range of messages older than the cutoff. Compare the oldest date with what Gmail shows for `before:` searches. Exit code 0.

- [ ] **Step 3: Reconcile the earlier attempt**

Run: `EmailArchive reconcile`
Expected: prints the number of messages found in `Gmail Archive` and `Gmail Archive/Sent`. In SQL Server Management Studio:

    SELECT State, COUNT(*) FROM EmailArchive.dbo.Messages GROUP BY State

shows that many rows in `AlreadyInTarget`. Run `EmailArchive report` again and confirm "Already in target" now matches.

- [ ] **Step 4: Verify the size assumption**

Run: `EmailArchive move --max-messages 5`
Expected: five oldest messages copied, verified, trashed. In the log, check whether any size warnings appeared and how far apart source and target sizes are:

    SELECT GmailUid, SourceSize, TargetSize, State FROM EmailArchive.dbo.Messages WHERE CopiedAt IS NOT NULL

If every pair is identical, tighten `Verifier.SizeWarningRatio` to a strict equality check in a follow-up change. If they differ by a few percent, leave it.

- [ ] **Step 5: Inspect in Outlook and Gmail**

In Outlook, open `Gmail Archive` and confirm the five messages appear with their original dates, read state, and star. In Gmail, open Trash and confirm the same five are there and no longer in All Mail.

- [ ] **Step 6: Larger batch**

Run: `EmailArchive move --max-messages 500`
Expected: exit code 0 or 1. Review any `Failed` rows:

    SELECT GmailUid, InternalDate, Subject, FailureReason FROM EmailArchive.dbo.Messages WHERE State = 'Failed'

Common causes: "2 matching copies in target" means the earlier attempt duplicated that message in Office 365; delete one copy in Outlook and rerun. "not found in target" after a successful append suggests Exchange indexing lag; rerun, the row resumes from `Copied`.

- [ ] **Step 7: Full backlog**

Repeat `EmailArchive move` (no cap, or `--max-bytes 2000000000` to stay under Gmail's daily IMAP bandwidth limit) until `report` shows zero messages to move. Then empty Gmail Trash by hand, or rerun with `--purge`, and confirm Gmail storage dropped.

- [ ] **Step 8: Schedule**

Follow `docs/setup-schedule.md`.

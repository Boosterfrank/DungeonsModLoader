using DungeonsModLoader.Core.Game;

namespace DungeonsModLoader.Core.Tests.Game;

/// <summary>A unique folder under the system temp folder, deleted on dispose.</summary>
public sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "dml-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    /// <summary>Combines <see cref="Path"/> with the given segments.</summary>
    public string Sub(params string[] segments) => System.IO.Path.Combine([Path, .. segments]);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best effort; a stray temp folder is harmless.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>Builds fake game folders shaped like the real install.</summary>
public static class FakeGame
{
    public const string RootExecutable = "Dungeons.exe";
    public const string ShippingExecutable = "Dungeons-Win64-Shipping.exe";

    /// <summary>
    /// Creates <c>root\Dungeons\Content\Paks</c> plus the optional dummy executables (root <c>Dungeons.exe</c> and
    /// <c>Dungeons\Binaries\Win64\Dungeons-Win64-Shipping.exe</c>). Returns <paramref name="root"/>.
    /// </summary>
    public static string CreateRoot(string root, bool rootExe = true, bool shippingExe = true)
    {
        Directory.CreateDirectory(Path.Combine(root, "Dungeons", "Content", "Paks"));
        if (rootExe)
        {
            File.WriteAllText(Path.Combine(root, RootExecutable), "stub");
        }

        if (shippingExe)
        {
            var binaries = Path.Combine(root, "Dungeons", "Binaries", "Win64");
            Directory.CreateDirectory(binaries);
            File.WriteAllText(Path.Combine(binaries, ShippingExecutable), "stub");
        }

        return root;
    }

    /// <summary>Writes a MicrosoftGame.config shaped like the real one into <paramref name="root"/>.</summary>
    public static void WriteMicrosoftGameConfig(string root, string displayName = "Minecraft Dungeons II", string identityName = "Microsoft.MinecraftDungeons2", string executableId = "AppMinecraftDungeonsIIShipping")
    {
        var xml = $"""
            <?xml version="1.0" encoding="utf-8"?>
            <Game configVersion="1">
              <Identity Name="{identityName}" Publisher="CN=Microsoft Corporation, O=Microsoft Corporation, L=Redmond, S=Washington, C=US" Version="1.1.1.0" />
              <ShellVisuals DefaultDisplayName="{displayName}" PublisherDisplayName="Microsoft Studios" StoreLogo="Resources\StoreLogo.png" />
              <ExecutableList>
                <Executable Name="Dungeons.exe" Id="{executableId}" OverrideDisplayName="ms-resource:AppDisplayName" TargetDeviceFamily="PC" />
              </ExecutableList>
            </Game>
            """;
        File.WriteAllText(Path.Combine(root, "MicrosoftGame.config"), "﻿" + xml);
    }

    /// <summary>A fake <see cref="IGameSourceLocator"/> returning fixed results (or throwing).</summary>
    public sealed class FixedSourceLocator : IGameSourceLocator
    {
        private readonly IReadOnlyList<GameInstallation> _results;
        private readonly Exception? _failure;

        public FixedSourceLocator(GameSource source, params GameInstallation[] results)
        {
            Source = source;
            _results = results;
        }

        public FixedSourceLocator(GameSource source, Exception failure)
        {
            Source = source;
            _results = Array.Empty<GameInstallation>();
            _failure = failure;
        }

        public GameSource Source { get; }

        public int Calls { get; private set; }

        public Task<IReadOnlyList<GameInstallation>> LocateAsync(CancellationToken cancellationToken = default)
        {
            Calls++;
            if (_failure is not null)
            {
                throw _failure;
            }

            return Task.FromResult(_results);
        }
    }
}

/// <summary>
/// A <see cref="FactAttribute"/> that only runs when the environment variable <c>DML_INTEGRATION</c> is <c>1</c>;
/// otherwise the test is reported as skipped. Used for tests that look at the real machine.
/// </summary>
public sealed class IntegrationFactAttribute : FactAttribute
{
    public const string EnvironmentVariable = "DML_INTEGRATION";

    public IntegrationFactAttribute()
    {
        if (Environment.GetEnvironmentVariable(EnvironmentVariable) != "1")
        {
            Skip = $"Integration test: set {EnvironmentVariable}=1 to run it against this machine.";
        }
    }
}

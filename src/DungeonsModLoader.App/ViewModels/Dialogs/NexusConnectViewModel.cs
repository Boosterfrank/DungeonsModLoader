using System.Runtime.InteropServices;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DungeonsModLoader.App.Services;
using DungeonsModLoader.Nexus;
using DungeonsModLoader.Nexus.Api;
using DungeonsModLoader.Nexus.Auth;
using Microsoft.Extensions.Logging;

namespace DungeonsModLoader.App.ViewModels.Dialogs;

/// <summary>
/// The "Connect your Nexus Mods account" dialog: three steps (open the API keys page, copy the personal key, paste
/// it) and a Connect button that verifies the key with Nexus and stores it encrypted. The key is never logged.
/// </summary>
public sealed partial class NexusConnectViewModel : ObservableObject
{
    /// <summary>Spec text shown under the key field.</summary>
    public const string KeyInfo =
        "Your key is stored encrypted on this PC only and is never uploaded anywhere except to Nexus Mods itself. Don't share your key with anyone.";

    public const string Step1Title = "Open your API keys page on Nexus Mods";
    public const string Step1Hint = "Log in on the site if it asks you to. The page opens in your browser.";
    public const string Step2Title = "Copy your personal key";
    public const string Step2Hint = "Scroll down to \"Personal API Key\". If there is no key yet, click \"Request an API key\". Then click \"Copy\".";
    public const string Step3Title = "Paste the key here and click Connect";

    private readonly INexusSession _session;
    private readonly IWindowService _windows;
    private readonly ILogger<NexusConnectViewModel> _logger;

    public NexusConnectViewModel(INexusSession session, IWindowService windows, ILogger<NexusConnectViewModel> logger)
    {
        _session = session;
        _windows = windows;
        _logger = logger;
    }

    /// <summary>Raised when the dialog should close; the argument is true after a successful connect.</summary>
    public event EventHandler<bool>? CloseRequested;

    /// <summary>Why the dialog came up ("Downloads use your Nexus Mods account."); null hides the line.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasReason))]
    private string? _reason;

    public bool HasReason => !string.IsNullOrWhiteSpace(Reason);

    /// <summary>The pasted key. Cleared after a successful connect; never written to the log.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConnectCommand))]
    private string _apiKeyInput = string.Empty;

    [ObservableProperty]
    private bool _showApiKey;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConnectCommand), nameof(PasteCommand), nameof(LoginWithSsoCommand))]
    private bool _isBusy;

    [ObservableProperty]
    private string? _busyText;

    /// <summary>Inline error under the key field; null hides it.</summary>
    [ObservableProperty]
    private string? _error;

    /// <summary>True once a key was verified and stored.</summary>
    [ObservableProperty]
    private bool _connected;

    public string KeyInfoText => KeyInfo;

    /// <summary>"Log in with Nexus" (browser approval) is offered once Nexus has issued the app slug.</summary>
    public bool IsSsoAvailable => NexusConstants.IsSsoAvailable;

    [RelayCommand]
    private void OpenApiKeyPage() => _windows.OpenUrl(NexusConstants.ApiKeyPageUrl);

    private bool CanPaste() => !IsBusy;

    /// <summary>Puts the clipboard text into the key field (the key is long; typing it is error-prone).</summary>
    [RelayCommand(CanExecute = nameof(CanPaste))]
    private void Paste()
    {
        string? text = null;
        try
        {
            if (Clipboard.ContainsText())
            {
                text = Clipboard.GetText();
            }
        }
        catch (Exception ex) when (ex is ExternalException or InvalidOperationException)
        {
            // Another program holds the clipboard; try again in a moment.
            _logger.LogDebug(ex, "Clipboard could not be read");
        }

        text = text?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            Error = "There is no text on the clipboard. Copy your key on the Nexus Mods page first, then click Paste.";
            return;
        }

        Error = null;
        ApiKeyInput = text;
    }

    private bool CanConnect() => !IsBusy && !string.IsNullOrWhiteSpace(ApiKeyInput);

    /// <summary>Verifies the key with Nexus Mods and stores it encrypted; closes the dialog on success.</summary>
    [RelayCommand(CanExecute = nameof(CanConnect))]
    private async Task ConnectAsync()
    {
        var provider = _session.Providers.FirstOrDefault(p => p.Method == NexusAuthMethod.PersonalApiKey);
        if (provider is null)
        {
            Error = "Personal API keys are not available in this build.";
            return;
        }

        await RunAsync("Checking the key with Nexus Mods...", async () =>
        {
            var user = await _session.LoginAsync(provider, ApiKeyInput.Trim());
            ApiKeyInput = string.Empty;
            Connected = true;
            _logger.LogInformation("Nexus Mods account connected through the connect dialog ({Membership})", user.IsPremium ? "premium" : "free");
            CloseRequested?.Invoke(this, true);
        });
    }

    private bool CanLoginWithSso() => !IsBusy && IsSsoAvailable;

    [RelayCommand(CanExecute = nameof(CanLoginWithSso))]
    private async Task LoginWithSsoAsync()
    {
        var provider = _session.Providers.FirstOrDefault(p => p.Method == NexusAuthMethod.Sso);
        if (provider is null)
        {
            return;
        }

        await RunAsync("Waiting for your approval in the browser...", async () =>
        {
            await _session.LoginAsync(provider, null, new Progress<string>(m => BusyText = m));
            Connected = true;
            CloseRequested?.Invoke(this, true);
        });
    }

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(this, false);

    private async Task RunAsync(string busyText, Func<Task> work)
    {
        Error = null;
        BusyText = busyText;
        IsBusy = true;
        try
        {
            await work();
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Connect cancelled");
        }
        catch (NexusException ex)
        {
            // Phrased for the user by the Nexus layer ("does not look like a key", "rejected", "could not be reached").
            _logger.LogWarning("Connect failed: {Message}", ex.Message);
            Error = ex.Message;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Connect failed unexpectedly");
            Error = "Something went wrong. See the log for details.";
        }
        finally
        {
            IsBusy = false;
            BusyText = null;
        }
    }
}

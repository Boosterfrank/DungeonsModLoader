using System.Windows;
using System.Windows.Controls;

namespace DungeonsModLoader.App.Controls;

/// <summary>
/// Attached properties used by the themed text inputs.
/// <para>
/// <c>Placeholder</c> is the watermark text shown by the TextBox / PasswordBox templates while the
/// field is empty: <c>controls:TextBoxHelper.Placeholder="Search mods..."</c>.
/// </para>
/// <para>
/// <c>HasText</c> is a read-only companion that the templates trigger on. It is kept up to date for
/// both <see cref="TextBox"/> (TextChanged) and <see cref="PasswordBox"/> (PasswordChanged, which has
/// no bindable Password property) once a placeholder has been assigned.
/// </para>
/// </summary>
public static class TextBoxHelper
{
    public static readonly DependencyProperty PlaceholderProperty = DependencyProperty.RegisterAttached(
        "Placeholder",
        typeof(string),
        typeof(TextBoxHelper),
        new FrameworkPropertyMetadata(string.Empty, OnPlaceholderChanged));

    private static readonly DependencyPropertyKey HasTextPropertyKey = DependencyProperty.RegisterAttachedReadOnly(
        "HasText",
        typeof(bool),
        typeof(TextBoxHelper),
        new FrameworkPropertyMetadata(false));

    /// <summary>True while the input contains text (or a password). Read-only; drives the watermark visibility.</summary>
    public static readonly DependencyProperty HasTextProperty = HasTextPropertyKey.DependencyProperty;

    // Private marker so the change handlers are hooked up exactly once per control.
    private static readonly DependencyProperty IsMonitoredProperty = DependencyProperty.RegisterAttached(
        "IsMonitored",
        typeof(bool),
        typeof(TextBoxHelper),
        new FrameworkPropertyMetadata(false));

    [AttachedPropertyBrowsableForType(typeof(TextBox))]
    [AttachedPropertyBrowsableForType(typeof(PasswordBox))]
    public static string GetPlaceholder(DependencyObject element) => (string)element.GetValue(PlaceholderProperty);

    public static void SetPlaceholder(DependencyObject element, string value) => element.SetValue(PlaceholderProperty, value);

    [AttachedPropertyBrowsableForType(typeof(TextBox))]
    [AttachedPropertyBrowsableForType(typeof(PasswordBox))]
    public static bool GetHasText(DependencyObject element) => (bool)element.GetValue(HasTextProperty);

    private static void OnPlaceholderChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        switch (d)
        {
            case TextBox textBox:
                if (!(bool)textBox.GetValue(IsMonitoredProperty))
                {
                    textBox.SetValue(IsMonitoredProperty, true);
                    textBox.TextChanged += (_, _) => Update(textBox);
                }

                Update(textBox);
                break;

            case PasswordBox passwordBox:
                if (!(bool)passwordBox.GetValue(IsMonitoredProperty))
                {
                    passwordBox.SetValue(IsMonitoredProperty, true);
                    passwordBox.PasswordChanged += (_, _) => Update(passwordBox);
                }

                Update(passwordBox);
                break;
        }
    }

    private static void Update(TextBox textBox) =>
        textBox.SetValue(HasTextPropertyKey, !string.IsNullOrEmpty(textBox.Text));

    private static void Update(PasswordBox passwordBox) =>
        passwordBox.SetValue(HasTextPropertyKey, passwordBox.SecurePassword.Length > 0);
}

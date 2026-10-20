using System.Windows.Markup;
using TunasDM.Core;

namespace TunasDM.App.Localization;

/// <summary>
/// Übersetzter Text in XAML: <c>Text="{l:T 'Speichern in'}"</c>. Der deutsche Text ist der Schlüssel (siehe <see cref="L"/>).
/// Die Sprache gilt ab dem Start; ein Wechsel in den Optionen wirkt nach dem Neustart.
/// </summary>
[MarkupExtensionReturnType(typeof(string))]
public sealed class TExtension(string text) : MarkupExtension
{
    public string Text { get; } = text;

    public override object ProvideValue(IServiceProvider serviceProvider) => L.T(Text);
}

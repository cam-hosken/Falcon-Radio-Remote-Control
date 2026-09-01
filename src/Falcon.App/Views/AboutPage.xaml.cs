using System.Globalization;
using Falcon.App.Core.ViewModels;

namespace Falcon.App.Views;

public partial class AboutPage : ContentPage
{
    public AboutPage()
    {
        InitializeComponent();
        // THIS app's version, read from the running package — never a
        // constant, which is a version that goes stale (§6 F6).
        VersionLabel.Text = AboutContent.VersionPrefix + AppInfo.Current.VersionString;

        // The byline's YEAR, under the same doctrine (round 13 C1): the
        // constant is the prefix; the year is read from the clock at display
        // time, so the copyright line cannot be a year out of date. Invariant
        // digits — a year is a number the operator reads, not localized copy.
        CreditLabel.Text =
            AboutContent.CreditPrefix + DateTime.Now.Year.ToString(CultureInfo.InvariantCulture) + ")";
    }
}

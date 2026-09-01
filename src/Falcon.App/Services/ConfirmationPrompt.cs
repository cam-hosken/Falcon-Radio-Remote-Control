using Falcon.App.Core.Services;

namespace Falcon.App.Services;

/// <summary>
/// The MAUI implementation of the §5 confirmation seam: a two-button alert on
/// the current page.
///
/// <para>This is the ONLY place the app turns a ViewModel's confirmation
/// request into UI. It lives in the app head because that is where MAUI is;
/// <see cref="IConfirmationPrompt"/> lives in the MAUI-free
/// <c>Falcon.App.Core</c> so ViewModels (and their host-only tests) never
/// reference a page.</para>
///
/// <para>No page yet (a request before the window exists, or after teardown)
/// answers <see langword="false"/>: an unanswerable question is a refusal, so
/// a destructive send never proceeds unasked.</para>
/// </summary>
public sealed class ConfirmationPrompt : IConfirmationPrompt
{
    public Task<bool> ConfirmAsync(string title, string message, string accept, string cancel)
    {
        var page = CurrentPage();
        return page is null
            ? Task.FromResult(false)
            : page.DisplayAlertAsync(title, message, accept, cancel);
    }

    private static Page? CurrentPage()
        => Shell.Current
           ?? Application.Current?.Windows.FirstOrDefault()?.Page;
}

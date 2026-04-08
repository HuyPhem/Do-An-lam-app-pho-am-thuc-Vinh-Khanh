using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TourGuideCMS.Services;

namespace TourGuideCMS.Pages;

[Authorize]
public class IndexModel : PageModel
{
    private readonly PlaceRepository _db;

    public IndexModel(PlaceRepository db) => _db = db;

    public int PlaceCount { get; private set; }
    public string DatabasePath { get; private set; } = "";

    public async Task OnGetAsync()
    {
        var list = await _db.ListAsync();
        PlaceCount = list.Count;
        DatabasePath = _db.DatabasePath;
    }
}

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TourGuideCMS.Models;
using TourGuideCMS.Services;

namespace TourGuideCMS.Pages.Places;

[Authorize]
public class IndexModel : PageModel
{
    private readonly PlaceRepository _db;

    public IndexModel(PlaceRepository db) => _db = db;

    public IReadOnlyList<PlaceRow> Places { get; private set; } = [];

    public async Task OnGetAsync() => Places = await _db.ListAsync();
}

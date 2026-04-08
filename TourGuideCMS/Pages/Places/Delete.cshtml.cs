using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TourGuideCMS.Models;
using TourGuideCMS.Services;

namespace TourGuideCMS.Pages.Places;

[Authorize]
public class DeleteModel : PageModel
{
    private readonly PlaceRepository _db;

    public DeleteModel(PlaceRepository db) => _db = db;

    public PlaceRow? Place { get; private set; }

    public async Task<IActionResult> OnGetAsync(int id)
    {
        Place = await _db.GetAsync(id);
        if (Place is null)
            return NotFound();
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(int id)
    {
        await _db.DeleteAsync(id);
        return RedirectToPage("Index");
    }
}

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TourGuideCMS.Models;
using TourGuideCMS.Services;

namespace TourGuideCMS.Pages.Places;

[Authorize]
public class EditModel : PageModel
{
    private readonly PlaceRepository _db;

    public EditModel(PlaceRepository db) => _db = db;

    [BindProperty]
    public PlaceFormViewModel Input { get; set; } = new();

    public async Task<IActionResult> OnGetAsync(int id)
    {
        var p = await _db.GetAsync(id);
        if (p is null)
            return NotFound();

        Input = new PlaceFormViewModel
        {
            Id = p.Id,
            Name = p.Name,
            Address = p.Address,
            Specialty = p.Specialty,
            ImageUrl = p.ImageUrl,
            Latitude = p.Latitude,
            Longitude = p.Longitude,
            ActivationRadiusMeters = p.ActivationRadiusMeters,
            Priority = p.Priority,
            Description = p.Description,
            VietnameseAudioText = p.VietnameseAudioText,
            EnglishAudioText = p.EnglishAudioText,
            ChineseAudioText = p.ChineseAudioText,
            JapaneseAudioText = p.JapaneseAudioText,
            MapUrl = p.MapUrl
        };

        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (!ModelState.IsValid)
            return Page();

        await _db.UpdateAsync(new PlaceRow
        {
            Id = Input.Id,
            Name = Input.Name,
            Address = Input.Address,
            Specialty = Input.Specialty,
            ImageUrl = Input.ImageUrl,
            Latitude = Input.Latitude,
            Longitude = Input.Longitude,
            ActivationRadiusMeters = Input.ActivationRadiusMeters,
            Priority = Input.Priority,
            Description = Input.Description,
            VietnameseAudioText = Input.VietnameseAudioText,
            EnglishAudioText = Input.EnglishAudioText,
            ChineseAudioText = Input.ChineseAudioText,
            JapaneseAudioText = Input.JapaneseAudioText,
            MapUrl = Input.MapUrl
        });

        return RedirectToPage("Index");
    }
}

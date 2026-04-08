using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TourGuideCMS.Models;
using TourGuideCMS.Services;

namespace TourGuideCMS.Pages.Places;

[Authorize]
public class CreateModel : PageModel
{
    private readonly PlaceRepository _db;

    public CreateModel(PlaceRepository db) => _db = db;

    [BindProperty]
    public PlaceFormViewModel Input { get; set; } = new();

    public void OnGet() { }

    public async Task<IActionResult> OnPostAsync()
    {
        if (!ModelState.IsValid)
            return Page();

        var row = new PlaceRow
        {
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
        };

        await _db.InsertAsync(row);
        return RedirectToPage("Index");
    }
}

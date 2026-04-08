using System.ComponentModel.DataAnnotations;

namespace TourGuideCMS.Models;

public sealed class PlaceFormViewModel
{
    public int Id { get; set; }

    [Required(ErrorMessage = "Bắt buộc")]
    public string Name { get; set; } = "";

    public string Address { get; set; } = "";
    public string Specialty { get; set; } = "";
    public string ImageUrl { get; set; } = "";
    public double Latitude { get; set; } = 10.7625;
    public double Longitude { get; set; } = 106.705;
    public double ActivationRadiusMeters { get; set; } = 35;
    public int Priority { get; set; }
    public string Description { get; set; } = "";
    public string VietnameseAudioText { get; set; } = "";
    public string EnglishAudioText { get; set; } = "";
    public string ChineseAudioText { get; set; } = "";
    public string JapaneseAudioText { get; set; } = "";
    public string MapUrl { get; set; } = "";
}

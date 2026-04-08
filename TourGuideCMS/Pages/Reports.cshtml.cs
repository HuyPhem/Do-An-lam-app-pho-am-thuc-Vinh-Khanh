using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace TourGuideCMS.Pages;

[Authorize]
public class ReportsModel : PageModel
{
    public void OnGet() { }
}

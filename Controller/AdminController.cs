using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.IO;

namespace MEAI_GPT_API.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class AdminController : ControllerBase
    {
        [HttpGet("download-db")]
        public IActionResult DownloadDatabase()
        {
            // 1. Strict Security Check: Only allow hamza.khan
            // Adjust "ClaimTypes.Name" based on how you set up your JWT claims
            var username = User.Identity?.Name ?? User.FindFirst("username")?.Value;
            if (string.IsNullOrEmpty(username) || username.ToLower() != "hamza.khan")
            {
                return Forbid("You are not authorized to download the database.");
            }

            // 2. Point to your actual database file path on the server
            // Change "MeaiDatabase.db" to the exact name of your EF Core SQLite file
            var dbPath = Path.Combine(Directory.GetCurrentDirectory(), "conversations.db");

            if (!System.IO.File.Exists(dbPath))
            {
                return NotFound("Database file not found on the server.");
            }

            // 3. Open the file with ReadWrite sharing so we don't crash EF Core while it's in use
            var stream = new FileStream(dbPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

            return File(stream, "application/octet-stream", "server-database.sqlite");
        }
    }
}
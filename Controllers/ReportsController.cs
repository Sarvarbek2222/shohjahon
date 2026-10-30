using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

/// <summary>Admin: kunlik aqlli tahlil (har kuni 00:00 dan keyin avtomatik tuziladi).</summary>
[Authorize(Roles = Roles.SuperAdmin)]
public class ReportsController : Controller
{
    private readonly AppDbContext _db;

    public ReportsController(AppDbContext db) => _db = db;

    public async Task<IActionResult> Index(string? day)
    {
        var days = await _db.DailyReports.AsNoTracking().OrderByDescending(r => r.Day).Select(r => r.Day).Take(60).ToListAsync();
        ViewBag.Days = days;
        DateTime? selected = DateTime.TryParse(day, out var d) ? d.Date : days.Cast<DateTime?>().FirstOrDefault();
        DailyReportData? data = null;
        if (selected is DateTime sd)
        {
            var row = await _db.DailyReports.AsNoTracking().FirstOrDefaultAsync(r => r.Day == sd);
            if (row != null) data = JsonSerializer.Deserialize<DailyReportData>(row.DataJson, DailyReportService.Json);
        }
        ViewBag.Selected = selected;
        ViewBag.IsToday = false;
        return View(data);
    }

    /// <summary>Bugungi holat — kun hali tugamagan, hisobot saqlanmaydi.</summary>
    public async Task<IActionResult> Today()
    {
        ViewBag.Days = await _db.DailyReports.AsNoTracking().OrderByDescending(r => r.Day).Select(r => r.Day).Take(60).ToListAsync();
        ViewBag.Selected = DailyReportService.LocalNow.Date;
        ViewBag.IsToday = true;
        return View("Index", await DailyReportService.BuildAsync(_db, DailyReportService.LocalNow.Date));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Build(string? day)
    {
        var target = DateTime.TryParse(day, out var d) ? d.Date : DailyReportService.LocalNow.Date.AddDays(-1);
        if (target >= DailyReportService.LocalNow.Date) target = DailyReportService.LocalNow.Date.AddDays(-1);
        await DailyReportService.BuildAndSaveAsync(_db, target);
        return RedirectToAction(nameof(Index), new { day = target.ToString("yyyy-MM-dd") });
    }

    /// <summary>"Xavf ostida" o'quvchilarga eslatma bildirishnomasi.</summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Remind(string day, [FromServices] NotificationService notify)
    {
        if (!DateTime.TryParse(day, out var d)) return RedirectToAction(nameof(Index));
        var row = await _db.DailyReports.AsNoTracking().FirstOrDefaultAsync(r => r.Day == d.Date);
        var data = row == null ? null : JsonSerializer.Deserialize<DailyReportData>(row.DataJson, DailyReportService.Json);
        var ids = data?.Students.AtRisk.Select(s => s.Id).ToList() ?? new();
        if (ids.Count > 0) await notify.NotifyUsersAsync(ids, "comeBack", new { }, "/");
        TempData["RepMsg"] = ids.Count;
        return RedirectToAction(nameof(Index), new { day = d.ToString("yyyy-MM-dd") });
    }
}

using Microsoft.EntityFrameworkCore;
using PeripheralMonitor.Server.Data;
using PeripheralMonitor.Shared.Models;

namespace PeripheralMonitor.Server.Services;

/// <summary>
/// Public contact-form submissions. There is no mail server, so messages are
/// persisted and read by SuperAdmin from the in-app inbox.
/// </summary>
public sealed class ContactService
{
    private const int MaxName = 120, MaxEmail = 200, MaxPhone = 50, MaxSubject = 200, MaxMessage = 5000;

    private readonly IDbContextFactory<AppDbContext> _db;
    private readonly ILogger<ContactService> _log;

    public ContactService(IDbContextFactory<AppDbContext> db, ILogger<ContactService> log)
    { _db = db; _log = log; }

    public async Task<(bool ok, string? error)> SubmitAsync(ContactSubmitRequest req, string? ip)
    {
        var name    = req.Name?.Trim()    ?? "";
        var email   = req.Email?.Trim()   ?? "";
        var phone   = req.Phone?.Trim();
        var subject = req.Subject?.Trim() ?? "";
        var message = req.Message?.Trim() ?? "";

        if (name.Length < 2)      return (false, "Please enter your name.");
        if (!LooksLikeEmail(email)) return (false, "Please enter a valid email address.");
        if (subject.Length < 2)   return (false, "Please enter a subject.");
        if (message.Length < 5)   return (false, "Please enter a message.");

        // Trim to column-sane lengths rather than rejecting outright
        if (name.Length > MaxName)       name = name[..MaxName];
        if (email.Length > MaxEmail)     email = email[..MaxEmail];
        if (phone is { Length: > MaxPhone }) phone = phone[..MaxPhone];
        if (subject.Length > MaxSubject) subject = subject[..MaxSubject];
        if (message.Length > MaxMessage) message = message[..MaxMessage];

        await using var db = await _db.CreateDbContextAsync();
        db.ContactMessages.Add(new ContactMessageEntity
        {
            Name = name, Email = email, Phone = string.IsNullOrWhiteSpace(phone) ? null : phone,
            Subject = subject, Message = message, Ip = ip, CreatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
        _log.LogInformation("Contact message received from {Email} ({Subject})", email, subject);
        return (true, null);
    }

    public async Task<List<ContactMessageDto>> GetAllAsync()
    {
        await using var db = await _db.CreateDbContextAsync();
        return await db.ContactMessages
            .AsNoTracking()
            .OrderByDescending(m => m.CreatedAt)
            .Select(m => new ContactMessageDto
            {
                Id = m.Id, Name = m.Name, Email = m.Email, Phone = m.Phone,
                Subject = m.Subject, Message = m.Message, IsRead = m.IsRead,
                CreatedAt = m.CreatedAt, Ip = m.Ip
            })
            .ToListAsync();
    }

    public async Task<int> GetUnreadCountAsync()
    {
        await using var db = await _db.CreateDbContextAsync();
        return await db.ContactMessages.CountAsync(m => !m.IsRead);
    }

    public async Task<bool> SetReadAsync(long id, bool read)
    {
        await using var db = await _db.CreateDbContextAsync();
        var m = await db.ContactMessages.FindAsync(id);
        if (m == null) return false;
        m.IsRead = read;
        await db.SaveChangesAsync();
        return true;
    }

    public async Task<bool> DeleteAsync(long id)
    {
        await using var db = await _db.CreateDbContextAsync();
        var m = await db.ContactMessages.FindAsync(id);
        if (m == null) return false;
        db.ContactMessages.Remove(m);
        await db.SaveChangesAsync();
        return true;
    }

    // Deliberately permissive: just enough to catch typos, not a full RFC validator.
    private static bool LooksLikeEmail(string s)
    {
        var at = s.IndexOf('@');
        if (at <= 0 || at == s.Length - 1) return false;
        var dot = s.IndexOf('.', at);
        return dot > at + 1 && dot < s.Length - 1 && !s.Contains(' ');
    }
}

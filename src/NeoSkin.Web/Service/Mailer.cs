using System.Net;
using System.Net.Mail;
using NeoSkin.Web.Config;

namespace NeoSkin.Web.Service;

/// <summary>
/// {@code Illuminate\Mail} 的最小移植（{@code Mail::to(...)->send(...)}）。
///
/// <p>支持的 mailer（{@code config('mail.default')} / {@code MAIL_MAILER}）：</p>
/// <ul>
///   <li>{@code smtp}（默认）：按 {@code MAIL_*} 直连 SMTP，失败抛异常；</li>
///   <li>{@code log}：把收件人/主题/正文写进应用日志；</li>
///   <li>{@code array}：只收集不发送（源用于测试）。</li>
/// </ul>
///
/// <p>与 Java 版同构：{@code log}/{@code array} 不抛异常；SMTP 未配置时抛异常。</p>
/// </summary>
public interface IMailer
{
    /// <summary>{@code config('mail.default')}</summary>
    string MailerName { get; }

    /// <summary>发送一封 HTML 邮件。</summary>
    /// <exception cref="InvalidOperationException">SMTP 未配置或发送失败</exception>
    void Send(string to, string subject, string body);
}

/// <summary>{@code IMailer} 的默认实现。</summary>
public sealed class Mailer(NeoSkinSettings settings, ILogger<Mailer>? logger = null) : IMailer
{
    private static readonly IReadOnlyDictionary<string, Action<Mailer, string, string, string>> _senders =
        new Dictionary<string, Action<Mailer, string, string, string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["log"] = (m, to, subject, body) => m.SendLog(to, subject, body),
            ["array"] = (m, to, subject, body) => { },
        };

    public string MailerName =>
        string.IsNullOrWhiteSpace(settings.Mail.Mailer) ? "smtp" : settings.Mail.Mailer.Trim().ToLowerInvariant();

    public void Send(string to, string subject, string body)
    {
        if (_senders.TryGetValue(MailerName, out var sender))
        {
            sender(this, to, subject, body);
            return;
        }

        SendSmtp(to, subject, body);
    }

    private void SendLog(string to, string subject, string body)
    {
        logger?.LogInformation("Mail (log mailer) to={To} subject={Subject} body={Body}", to, subject, body);
    }

    private void SendSmtp(string to, string subject, string body)
    {
        var mail = settings.Mail;
        if (string.IsNullOrWhiteSpace(mail.Host))
        {
            throw new InvalidOperationException("SMTP host is not configured");
        }

        using var client = new SmtpClient(mail.Host, mail.Port)
        {
            EnableSsl = string.Equals(mail.Encryption, "tls", StringComparison.OrdinalIgnoreCase),
            UseDefaultCredentials = string.IsNullOrEmpty(mail.Username),
        };

        if (!string.IsNullOrEmpty(mail.Username))
        {
            client.Credentials = new NetworkCredential(mail.Username, mail.Password ?? "");
        }

        var fromAddress = string.IsNullOrWhiteSpace(mail.FromAddress)
            ? "noreply@localhost"
            : mail.FromAddress!;
        var fromName = string.IsNullOrWhiteSpace(mail.FromName) ? "" : mail.FromName!;
        var from = string.IsNullOrEmpty(fromName)
            ? new MailAddress(fromAddress)
            : new MailAddress(fromAddress, fromName);

        using var message = new MailMessage(from, new MailAddress(to))
        {
            Subject = subject,
            Body = body,
            IsBodyHtml = true,
            SubjectEncoding = System.Text.Encoding.UTF8,
            BodyEncoding = System.Text.Encoding.UTF8,
        };

        message.Headers.Add("Auto-Submitted", "auto-generated");

        client.Send(message);
    }
}

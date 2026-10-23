using System.Reflection;
using System.Text.Json;
using Walis.Core.Models;

namespace Walis.Api.Services;

/// <summary>
///     Turns a settings update into audit records that say which fields changed, never what they changed to: the settings hold
///     SMTP passwords, webhook secrets and cloud credentials, and the audit table is readable by every administrator and may be exported.
///     The few numeric or boolean security fields get a dedicated record with the old and new value spelled out.
/// </summary>
public static class SettingsChangeDescriber
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private static readonly PropertyInfo[] Properties = typeof(WalisSettings)
        .GetProperties(BindingFlags.Public | BindingFlags.Instance)
        .Where(property => property.CanRead && property.GetIndexParameters().Length == 0)
        .ToArray();

    /// <summary>The camelCase names of the fields whose serialized value differs; BackupTargets counts as one field.</summary>
    public static IReadOnlyList<string> ChangedFields(WalisSettings before, WalisSettings after)
    {
        var changed = new List<string>();

        foreach (var property in Properties)
        {
            var left = Normalize(JsonSerializer.Serialize(property.GetValue(before), property.PropertyType, SerializerOptions));
            var right = Normalize(JsonSerializer.Serialize(property.GetValue(after), property.PropertyType, SerializerOptions));

            if (!string.Equals(left, right, StringComparison.Ordinal))
                changed.Add(SerializerOptions.PropertyNamingPolicy!.ConvertName(property.Name));
        }

        return changed;
    }

    /// <summary>The form sends an empty string where the stored value is null; that is not a change worth a record.</summary>
    private static string Normalize(string json)
    {
        return json == "\"\"" ? "null" : json;
    }

    private static bool SecretChanged(string? before, string? after)
    {
        return !string.Equals(before ?? string.Empty, after ?? string.Empty, StringComparison.Ordinal);
    }

    /// <summary>The ordinary "settings updated" record listing the changed field names.</summary>
    public static AuditEvent Describe(IReadOnlyList<string> changedFields)
    {
        var summary = changedFields.Count == 0
            ? "更新系統設定（沒有欄位變更）"
            : $"更新系統設定：{string.Join("、", changedFields)}";

        return new AuditEvent(AuditAction.Update, "settings", null, summary);
    }

    /// <summary>
    ///     One extra record per security-relevant field that changed, so that switching auditing off, shortening retention or
    ///     turning sign-in off cannot hide inside a generic "settings updated" line. The record about the audit switch is forced
    ///     through even when auditing is (or is about to be) off.
    /// </summary>
    public static IEnumerable<AuditEvent> DescribeSecurityChanges(WalisSettings before, WalisSettings after)
    {
        if (before.AuditLog != after.AuditLog)
            yield return new AuditEvent(AuditAction.Update, "settings", "auditLog",
                after.AuditLog ? "開啟稽核紀錄" : "關閉稽核紀錄") { Force = true };

        if (before.AuditRetentionDays != after.AuditRetentionDays)
            yield return new AuditEvent(AuditAction.Update, "settings", "auditRetentionDays",
                $"稽核保留天數由 {before.AuditRetentionDays} 改為 {after.AuditRetentionDays}") { Force = true };

        if (before.LogRetentionDays != after.LogRetentionDays)
            yield return new AuditEvent(AuditAction.Update, "settings", "logRetentionDays",
                $"紀錄保留天數由 {before.LogRetentionDays} 改為 {after.LogRetentionDays}");

        if (before.RequireSignIn != after.RequireSignIn)
            yield return new AuditEvent(AuditAction.Update, "settings", "requireSignIn",
                after.RequireSignIn ? "啟用管理端登入" : "關閉管理端登入");

        if (before.RequireOtp != after.RequireOtp)
            yield return new AuditEvent(AuditAction.Update, "settings", "requireOtp",
                after.RequireOtp ? "要求所有帳號啟用一次性密碼" : "取消要求一次性密碼");

        if (SecretChanged(before.WebhookSecret, after.WebhookSecret))
            yield return new AuditEvent(AuditAction.Update, "settings", "webhookSecret",
                string.IsNullOrEmpty(after.WebhookSecret) ? "清除 Webhook 密鑰" : "變更 Webhook 密鑰");

        if (SecretChanged(before.NotificationSigningSecret, after.NotificationSigningSecret))
            yield return new AuditEvent(AuditAction.Update, "settings", "notificationSigningSecret",
                string.IsNullOrEmpty(after.NotificationSigningSecret) ? "清除通知簽章密鑰" : "變更通知簽章密鑰");

        if (before.AllowUnisolatedBuilds != after.AllowUnisolatedBuilds)
            yield return new AuditEvent(AuditAction.Update, "settings", "allowUnisolatedBuilds",
                after.AllowUnisolatedBuilds ? "允許在沒有隔離邊界時執行建置" : "沒有隔離邊界時拒絕執行建置");
    }
}

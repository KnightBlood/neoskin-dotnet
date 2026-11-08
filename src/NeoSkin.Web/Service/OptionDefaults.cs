using System.Collections.ObjectModel;

namespace NeoSkin.Web.Service;

/// <summary>
/// <c>config/options.php</c> 的默认值表（54 个键，顺序与源文件一致）。
///
/// <p><c>Option::get()</c> 在数据库/缓存里找不到键时回退到这里，<b>并把默认值写回数据库</b>。
/// <c>max_texture_width</c> 不在这张表里：它由 migration 单独写入 8192。</p>
/// </summary>
public static class OptionDefaults
{
    private static readonly IReadOnlyDictionary<string, string> Values =
        new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["site_url"] = "",
            ["site_name"] = "NeoSkin",
            ["site_description"] = "NeoSkin Minecraft Skin Hosting Service",
            ["register_with_player_name"] = "true",
            ["require_verification"] = "false",
            ["regs_per_ip"] = "3",
            ["announcement"] = "Welcome to NeoSkin {version}!",
            ["home_pic_url"] = "./app/bg.webp",
            ["custom_css"] = "",
            ["custom_js"] = "",
            ["player_name_rule"] = "official",
            ["custom_player_name_regexp"] = "",
            ["player_name_length_min"] = "3",
            ["player_name_length_max"] = "16",
            ["user_initial_score"] = "1000",
            ["sign_gap_time"] = "24",
            ["sign_score"] = "10,100",
            ["score_per_storage"] = "true",
            ["private_score_per_storage"] = "10",
            ["return_score"] = "true",
            ["score_per_player"] = "100",
            ["sign_after_zero"] = "false",
            ["version"] = "",
            ["copyright_text"] =
                "<b>Copyright &copy; {year} <a href=\"{site_url}\">{site_name}</a>.</b> All rights reserved.",
            ["auto_del_invalid_texture"] = "false",
            ["allow_downloading_texture"] = "true",
            ["texture_name_regexp"] = "",
            ["cache_expire_time"] = "31536000",
            ["max_upload_file_size"] = "1024",
            ["force_ssl"] = "false",
            ["auto_detect_asset_url"] = "true",
            ["plugins_enabled"] = "",
            ["copyright_prefer"] = "0",
            ["score_per_closet_item"] = "0",
            ["favicon_url"] = "app/favicon.ico",
            ["score_award_per_texture"] = "0",
            ["take_back_scores_after_deletion"] = "true",
            ["score_award_per_like"] = "0",
            ["meta_keywords"] = "",
            ["meta_description"] = "",
            ["meta_extras"] = "",
            ["cdn_address"] = "",
            ["update_source"] = "",
            ["plugins_registry"] = "",
            ["recaptcha_sitekey"] = "",
            ["recaptcha_secretkey"] = "",
            ["recaptcha_invisible"] = "false",
            ["reporter_score_modification"] = "0",
            ["reporter_reward_score"] = "0",
            ["content_policy"] = "",
            ["transparent_navbar"] = "false",
            ["status_code_for_private"] = "403",
            ["navbar_color"] = "cyan",
            ["sidebar_color"] = "dark-maroon",
        });

    /// <summary>不可变的默认值表（保持 config/options.php 的键顺序）。</summary>
    public static IReadOnlyDictionary<string, string> All => Values;

    public static string? Get(string key) => Values.TryGetValue(key, out string? value) ? value : null;

    public static bool Has(string key) => Values.ContainsKey(key);
}

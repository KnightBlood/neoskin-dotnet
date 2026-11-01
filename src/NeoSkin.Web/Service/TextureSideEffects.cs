using NeoSkin.Web.Db;
using NeoSkin.Web.Support;
using NeoSkin.Web.Web.Input;

namespace NeoSkin.Web.Service;

/// <summary>
/// 源项目 <c>texture.deleted</c> / <c>texture.privacy.updated</c> 三个监听器的移植：
/// <c>UpdateScoreForDeletedTexture</c>、<c>ResetPlayers</c>、<c>CleanUpCloset</c>。
/// </summary>
public static class TextureSideEffects
{
    /// <summary><c>texture.deleted</c>。</summary>
    public static void OnDeleted(Database db, Option option, TextureRow texture,
        UserRepository users, PlayerRepository players, ClosetRepository closet,
        TextureRepository textures)
    {
        RefundScore(db, option, texture, users);
        ResetPlayers(players, texture, null);
        CleanUpCloset(db, option, texture, textureStillExists: false, users, closet, textures);
    }

    /// <summary><c>texture.privacy.updated</c>（只会是「公开 → 私有」）。</summary>
    public static void OnPrivacyUpdated(Database db, Option option, TextureRow texture,
        UserRepository users, PlayerRepository players, ClosetRepository closet,
        TextureRepository textures)
    {
        if (texture.IsPublic)
        {
            return;
        }

        ResetPlayers(players, texture, texture.Uploader);
        CleanUpCloset(db, option, texture, textureStillExists: true, users, closet, textures);
    }

    /// <summary><c>UpdateScoreForDeletedTexture</c>：退还存储分并扣回上传奖励。</summary>
    public static void RefundScore(Database db, Option option, TextureRow texture,
        UserRepository users)
    {
        UserRepository.UserRow? uploader = users.FindById(texture.Uploader);
        if (uploader is null)
        {
            return;
        }

        long ret = 0;
        if (RequestInput.ToBool(option.Get("return_score", true)))
        {
            ret += texture.Size * (texture.IsPublic
                ? option.GetInt("score_per_storage", 1)
                : option.GetInt("private_score_per_storage", 10));
        }

        if (texture.IsPublic && RequestInput.ToBool(option.Get("take_back_scores_after_deletion", true)))
        {
            ret -= option.GetInt("score_award_per_texture", 0);
        }

        users.UpdateScore(uploader.Uid, (int)(uploader.Score + ret));
    }

    /// <summary><c>ResetPlayers</c>。</summary>
    public static void ResetPlayers(PlayerRepository players, TextureRow texture,
        int? excludeUid)
    {
        string column = texture.Type == "cape" ? "tid_cape" : "tid_skin";
        players.ResetTexture(texture.Tid, column, excludeUid);
    }

    /// <summary><c>CleanUpCloset</c>。</summary>
    public static void CleanUpCloset(Database db, Option option, TextureRow texture,
        bool textureStillExists, UserRepository users, ClosetRepository closet,
        TextureRepository textures)
    {
        List<int> likers = closet.LikerUids(texture.Tid, texture.Uploader);
        if (likers.Count == 0)
        {
            return;
        }

        bool returnScore = RequestInput.ToBool(option.Get("return_score", true));
        int refund = option.GetInt("score_per_closet_item", 0);

        foreach (int uid in likers)
        {
            closet.Detach(uid, texture.Tid);
            if (returnScore)
            {
                UserRepository.UserRow? user = users.FindById(uid);
                if (user is not null)
                {
                    users.UpdateScore(uid, user.Score + refund);
                }
            }
        }

        if (textureStillExists)
        {
            textures.UpdateLikes(texture.Tid, texture.Likes - likers.Count);
        }
    }
}

namespace NeoSkin.Web.Render;

/// <summary>
/// <c>Blessing\Renderer\SkinRenderer</c> 的移植（Java 版结构逐段对齐）。
///
/// <p>刻意保留源码里的行为怪癖（bug-for-bug）：</p>
/// <list type="bullet">
///   <item>体积点表按「索引三元组」键控，且四肢侧面的守卫检查的是
///         <c>[8*hd][j][k]</c>，实际写入 <c>[4*hd][j][k]</c>（上游原样）；</item>
///   <item>「背面朝向」取的是 depth 较小的一侧，比较用严格小于语义；</item>
///   <item><c>MemberRotation</c> 对腿部的 z 偏移按 <c>sinAlpha &lt; 0 ? 0 : 4</c>。</item>
/// </list>
///
/// <p>键控细节：Java 的键是 <c>i + "|" + j + "|" + k</c>，int 与 double 的
/// 字符串不同（<c>"8"</c> vs <c>"8.0"</c>），这个差异决定了上面那条
/// 「守卫检查 8*hd、写入 4*hd」的怪癖是否复现。这里用 <see cref="Num"/>
/// 保留 int/double 两种来源。</p>
/// </summary>
public sealed class SkinRenderer
{
    /// <summary>单个立方体角点及其关联面。</summary>
    private sealed record CubePoint(Point3 Point, IReadOnlyList<string> Faces);

    /// <summary><c>getDisplayOrder()</c> 的单个绘制批次（piece → faces）。</summary>
    private sealed record DrawPass(string Piece, IReadOnlyList<string> Faces);

    /// <summary><c>members_angles</c> 的成员角（cos/sin 四元组）。</summary>
    private sealed record Angles(double CosAlpha, double SinAlpha, double CosOmega, double SinOmega);

    private static readonly string[] AllFaceNames =
        ["back", "right", "top", "front", "left", "bottom"];

    private static readonly string[] Members =
        ["head", "torso", "rightArm", "leftArm", "rightLeg", "leftLeg"];

    private static readonly string[] Pieces =
        ["helmet", "head", "torso", "rightArm", "leftArm", "rightLeg", "leftLeg"];

    private Canvas? _playerSkin;
    private bool _isNewSkinType;
    private bool _isAlex;

    private double _hdRatio = 1; // PHP 为浮点除 imagesx/64

    private readonly double _vR;
    private readonly double _hR;
    private readonly double _hrh;
    private readonly double _vrll;
    private readonly double _vrrl;
    private readonly double _vrla;
    private readonly double _vrra;
    private readonly bool _headOnly;
    private readonly double _ratio;
    private readonly bool _layers;

    // Rotation variables in radians (3D Rendering)
    private double _alpha; // Vertical rotation on the X axis.
    private double _omega; // Horizontal rotation on the Y axis.
    private double _cosAlpha;
    private double _sinAlpha;
    private double _cosOmega;
    private double _sinOmega;

    private Dictionary<string, Angles> _membersAngles = new(StringComparer.Ordinal);

    private Point3.Bounds _bounds = new();

    private IReadOnlyList<string> _allFaces = [];
    private IReadOnlyList<string> _frontFaces = [];
    private IReadOnlyList<string> _backFaces = [];

    private readonly Dictionary<string, FacePair> _visibleFaces = new(StringComparer.Ordinal);
    private List<CubePoint> _cubePoints = [];

    private readonly Dictionary<string, Dictionary<string, List<FacePolygon>>> _polygons =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, Point3> _volumePoints = new(StringComparer.Ordinal);

    private sealed record FacePair(IReadOnlyList<string> Back, IReadOnlyList<string> Front);

    public SkinRenderer(double ratio, bool headOnly, double horizontalRotation,
        double verticalRotation, double horizontalRotationOfHead,
        double verticalRotationOfLeftLeg, double verticalRotationOfRightLeg,
        double verticalRotationOfLeftArm, double verticalRotationOfRightArm, bool layers)
    {
        _ratio = ratio;
        _headOnly = headOnly;
        _hR = horizontalRotation;
        _vR = verticalRotation;
        _hrh = horizontalRotationOfHead;
        _vrll = verticalRotationOfLeftLeg;
        _vrrl = verticalRotationOfRightLeg;
        _vrla = verticalRotationOfLeftArm;
        _vrra = verticalRotationOfRightArm;
        _layers = layers;
    }

    /// <summary>PHP <c>new SkinRenderer($ratio, $headOnly, $horizontalRotation)</c>：vertical 默认 -25。</summary>
    public SkinRenderer(double ratio, bool headOnly, double horizontalRotation)
        : this(ratio, headOnly, horizontalRotation, -25, 0, 0, 0, 0, 0, true)
    {
    }

    public SkinRenderer(double ratio, bool headOnly, double horizontalRotation,
        double verticalRotation)
        : this(ratio, headOnly, horizontalRotation, verticalRotation, 0, 0, 0, 0, 0, true)
    {
    }

    /// <summary><c>render()</c>。</summary>
    public Canvas? Render(byte[] source, bool isAlex)
    {
        _playerSkin = Canvas.FromPng(source);
        if (_playerSkin is null)
        {
            return null;
        }

        int sourceWidth = _playerSkin.Width;
        int sourceHeight = _playerSkin.Height;

        // prevent memory overflow
        if (sourceWidth > 256)
        {
            double newHeightD = sourceHeight / (sourceWidth / 256.0);
            int newHeight = (int)newHeightD;
            Canvas dest = Canvas.CreateEmpty(256, newHeight);
            dest.ResampleRegion(_playerSkin, 0, 0, 256, newHeight,
                0, 0, _playerSkin.Width, _playerSkin.Height);
            _playerSkin = dest;
        }

        _isAlex = isAlex;
        _hdRatio = _playerSkin.Width / 64.0; // 以宽度为准（新款皮肤宽度未必等于高度）

        // check if new skin type. If both sides are equally long: new skin type
        if (_playerSkin.Width == _playerSkin.Height)
        {
            _isNewSkinType = true;
        }

        MakeBackgroundTransparent(); // fix for weird rendering skins

        // Quick fix for 1.8: copy the extra layers ontop of the base layers
        if (_layers)
        {
            FixNewSkinTypeLayers();
        }

        CalculateAngles();
        FacesDetermination();
        GeneratePolygons();
        MemberRotation();
        CreateProjectionPlan();
        Canvas image = DisplayImage();
        _playerSkin = null;

        return image;
    }

    // ======================================================== background

    private void MakeBackgroundTransparent()
    {
        Canvas skin = _playerSkin!;

        // check if the corner box is one solid color
        int tempValue = int.MinValue;
        bool needRemove = true;

        for (int iH = 0; iH < 8; iH++)
        {
            for (int iV = 0; iV < 8; iV++)
            {
                int pixelColor = skin.ColorAtGd(iH, iV);

                int gdAlpha = (pixelColor >>> 24) & 0x7F;
                if (gdAlpha > 120)
                {
                    // the image contains transparency, nothing to do
                    needRemove = false;
                }

                if (tempValue == int.MinValue)
                {
                    tempValue = pixelColor;
                }
                else if (tempValue != pixelColor)
                {
                    // Cannot determine a background color, file is probably fine
                    needRemove = false;
                }
            }
        }

        int imgX = skin.Width;
        int imgY = skin.Height;
        Canvas dst = Canvas.CreateEmpty(imgX, imgY);

        int fillGd;
        int bgRgb = 0;

        if (needRemove)
        {
            // the entire block is one solid color. Use this color to clear the background.
            bgRgb = tempValue & 0x00FFFFFF;
            // imagecolorallocate → 不透明填色
            fillGd = bgRgb;
        }
        else
        {
            // imagecolorallocate($dst, 0, 0, 0) 是**不透明黑**：先铺底，
            // 让皮肤里的透明像素在这些区域内显示为黑色而不是空洞（PHP 实测）。
            fillGd = 0;
        }

        // fill the areas that should not be transparent
        double positionMultiply = imgX / 64.0;

        // head
        RectF(dst, 8, 0, 23, 7, positionMultiply, fillGd);
        RectF(dst, 0, 8, 31, 15, positionMultiply, fillGd);

        // right leg, body, right arm
        RectF(dst, 4, 16, 11, 19, positionMultiply, fillGd);
        RectF(dst, 20, 16, 35, 19, positionMultiply, fillGd);
        RectF(dst, 44, 16, 51, 19, positionMultiply, fillGd);
        RectF(dst, 0, 20, 54, 31, positionMultiply, fillGd);

        // left leg, left arm
        RectF(dst, 20, 48, 27, 51, positionMultiply, fillGd);
        RectF(dst, 36, 48, 43, 51, positionMultiply, fillGd);
        RectF(dst, 16, 52, 47, 63, positionMultiply, fillGd);

        // imagecopy($dst, $playerSkin, 0, 0, 0, 0, $imgX, $imgY)
        // 被标记为「背景色」或全透明的源像素不覆盖底色（imagecolortransparent 的等效）
        for (int y = 0; y < imgY; y++)
        {
            for (int x = 0; x < imgX; x++)
            {
                int src = skin.PixelAt(x, y);

                if (needRemove && (src & 0x00FFFFFF) == bgRgb)
                {
                    continue;
                }

                int srcGdAlpha = 127 - JavaRound(((src >>> 24) & 0xFF) * 127 / 255f);
                if (srcGdAlpha >= 127)
                {
                    continue;
                }

                dst.BlendPixel(x, y, src);
            }
        }

        _playerSkin = dst;
    }

    private static void RectF(Canvas dst, int x1, int y1, int x2, int y2, double mul,
        int gdColor) =>
        dst.FilledRectangle((int)(x1 * mul), (int)(y1 * mul),
            (int)(x2 * mul), (int)(y2 * mul), gdColor);

    /// <summary>Java <c>Math.round(float)</c>：floor(x + 0.5)。</summary>
    private static int JavaRound(float value) => (int)MathF.Floor(value + 0.5f);

    /// <summary>
    /// Function copies the extra layers of a 1.8 skin onto the base layers so that it
    /// will still show. QUICK FIX, NEEDS BETTER FIX.
    /// </summary>
    private void FixNewSkinTypeLayers()
    {
        if (!_isNewSkinType)
        {
            return;
        }

        Canvas skin = _playerSkin!;
        CopySelf(skin, 0, 16, 0, 32, 56, 16); // RL2, BODY2, RA2
        CopySelf(skin, 16, 48, 0, 48, 16, 16); // LL2
        CopySelf(skin, 32, 48, 48, 48, 16, 16); // LA2
    }

    /// <summary>
    /// PHP <c>imagecopy($skin, $skin, ...)</c>：自拷贝。GD 默认开启 alpha blending，
    /// 因此全透明的源像素不会覆盖目标（这正是「第二层透明时不会擦掉基础层」的原因）。
    /// 先快照再写，避免重叠区域的读写干扰。
    /// </summary>
    private static void CopySelf(Canvas img, int dx, int dy, int sx, int sy, int w, int h)
    {
        var snapshot = new List<(int X, int Y, int Color)>();
        for (int j = 0; j < h; j++)
        {
            for (int i = 0; i < w; i++)
            {
                snapshot.Add((dx + i, dy + j, img.PixelAt(sx + i, sy + j)));
            }
        }

        foreach ((int x, int y, int color) in snapshot)
        {
            img.BlendPixel(x, y, color);
        }
    }

    // ======================================================== angles

    private void CalculateAngles()
    {
        _alpha = _vR * Math.PI / 180.0; // Vertical rotation on the X axis.
        _omega = _hR * Math.PI / 180.0; // Horizontal rotation on the Y axis.

        _cosAlpha = Math.Cos(_alpha);
        _sinAlpha = Math.Sin(_alpha);
        _cosOmega = Math.Cos(_omega);
        _sinOmega = Math.Sin(_omega);

        _membersAngles = new Dictionary<string, Angles>(StringComparer.Ordinal)
        {
            ["torso"] = AnglesOf(0, 0),
        };

        double alphaHead = 0;
        double omegaHead = _hrh * Math.PI / 180.0;
        _membersAngles["head"] = AnglesOf(alphaHead, omegaHead);
        _membersAngles["helmet"] = _membersAngles["head"]; // Head and helmet get the same

        _membersAngles["rightArm"] = AnglesOf(_vrra * Math.PI / 180.0, 0);
        _membersAngles["leftArm"] = AnglesOf(_vrla * Math.PI / 180.0, 0);
        _membersAngles["rightLeg"] = AnglesOf(_vrrl * Math.PI / 180.0, 0);
        _membersAngles["leftLeg"] = AnglesOf(_vrll * Math.PI / 180.0, 0);

        _bounds = new Point3.Bounds(); // minX = maxX = minY = maxY = 0
    }

    private static Angles AnglesOf(double alphaRad, double omegaRad) =>
        new(Math.Cos(alphaRad), Math.Sin(alphaRad), Math.Cos(omegaRad), Math.Sin(omegaRad));

    // ======================================================== faces

    private void FacesDetermination()
    {
        double ca = _cosAlpha;
        double sa = _sinAlpha;
        double co = _cosOmega;
        double so = _sinOmega;

        _visibleFaces.Clear();
        foreach (string part in Members)
        {
            _visibleFaces[part] = new FacePair([], []);
        }

        _allFaces = AllFaceNames.ToArray();

        // Loop each preProject and Project then calculate the visible faces for each
        foreach (string part in Members)
        {
            CubePoint? cubeMaxDepthFaces = null;

            SetCubePoints();

            foreach (CubePoint cubePoint in _cubePoints)
            {
                Angles m = _membersAngles[part];
                cubePoint.Point.PreProject(0, 0, 0,
                    m.CosAlpha, m.SinAlpha, m.CosOmega, m.SinOmega);
                cubePoint.Point.Project(ca, sa, co, so, _bounds);

                if (cubeMaxDepthFaces is null)
                {
                    cubeMaxDepthFaces = cubePoint;
                }
                else if (cubeMaxDepthFaces.Point.Depth(ca, sa, co, so, _bounds)
                         > cubePoint.Point.Depth(ca, sa, co, so, _bounds))
                {
                    cubeMaxDepthFaces = cubePoint;
                }
            }

            IReadOnlyList<string> back = cubeMaxDepthFaces!.Faces;
            _visibleFaces[part] = new FacePair(back, ArrayDiff(_allFaces, back));
        }

        SetCubePoints();

        CubePoint? maxDepth = null;
        foreach (CubePoint cubePoint in _cubePoints)
        {
            cubePoint.Point.Project(ca, sa, co, so, _bounds);

            if (maxDepth is null)
            {
                maxDepth = cubePoint;
            }
            else if (maxDepth.Point.Depth(ca, sa, co, so, _bounds)
                     > cubePoint.Point.Depth(ca, sa, co, so, _bounds))
            {
                maxDepth = cubePoint;
            }

            _backFaces = maxDepth.Faces;
            _frontFaces = ArrayDiff(_allFaces, _backFaces);
        }
    }

    /// <summary><c>array_diff($all, $remove)</c>：保留 <paramref name="all"/> 中的相对顺序。</summary>
    private static IReadOnlyList<string> ArrayDiff(IReadOnlyList<string> all,
        IReadOnlyList<string> remove)
    {
        var result = new List<string>();
        foreach (string face in all)
        {
            if (!remove.Contains(face))
            {
                result.Add(face);
            }
        }

        return result;
    }

    private void SetCubePoints()
    {
        _cubePoints =
        [
            new CubePoint(new Point3(0, 0, 0), ["back", "right", "top"]), // 0
            new CubePoint(new Point3(0, 0, 1), ["front", "right", "top"]), // 1
            new CubePoint(new Point3(0, 1, 0), ["back", "right", "bottom"]), // 2
            new CubePoint(new Point3(0, 1, 1), ["front", "right", "bottom"]), // 3
            new CubePoint(new Point3(1, 0, 0), ["back", "left", "top"]), // 4
            new CubePoint(new Point3(1, 0, 1), ["front", "left", "top"]), // 5
            new CubePoint(new Point3(1, 1, 0), ["back", "left", "bottom"]), // 6
            new CubePoint(new Point3(1, 1, 1), ["front", "left", "bottom"]), // 7
        ];
    }

    // ======================================================== polygons

    private void GeneratePolygons()
    {
        _polygons.Clear();

        foreach (string part in Pieces)
        {
            var m = new Dictionary<string, List<FacePolygon>>(StringComparer.Ordinal);
            foreach (string f in AllFaceNames)
            {
                m[f] = [];
            }

            _polygons[part] = m;
        }

        Head();
        Helmet();
        if (!_headOnly)
        {
            Torso();
            RightArm();
            LeftArm();
            RightLeg();
            LeftLeg();
        }
    }

    // ---------------------------------------------------------------- 体积点表

    /// <summary>
    /// PHP <c>$volume_points[$i][$j][$k]</c> 的键语义：浮点数组键会被**截断为整数**，
    /// 因此 <c>[8 * $hd]</c>（8.0）与循环里的 <c>[8]</c> 是同一个键。这个截断决定了
    /// 源码里「守卫检查 8*hd、写入 4*hd」的怪癖是否复现（Java 版的字符串键区分
    /// int/double，属于偏差；本端口按 PHP 对齐）。
    /// </summary>
    private static string Key(double i, double j, double k) =>
        (long)i + "|" + (long)j + "|" + (long)k;

    private bool Has(double i, double j, double k) => _volumePoints.ContainsKey(Key(i, j, k));

    private void Put(double i, double j, double k, double x, double y, double z) =>
        _volumePoints[Key(i, j, k)] = new Point3(x, y, z);

    private Point3 Pt(double i, double j, double k)
    {
        string key = Key(i, j, k);
        if (!_volumePoints.TryGetValue(key, out Point3? point))
        {
            throw new InvalidOperationException(
                "volume point missing: " + key + " (part under construction)");
        }

        return point;
    }

    private int Color(int x, int y) => _playerSkin!.ColorAtGd(x, y);

    private void Quad(string part, string face, Point3 a, Point3 b, Point3 c, Point3 d,
        int color) =>
        _polygons[part][face].Add(new FacePolygon([a, b, c, d], color));

    // ---------------------------------------------------------------- HEAD

    private void Head()
    {
        _volumePoints.Clear();
        double hd = _hdRatio;

        for (int i = 0; i < 9 * hd; i++)
        {
            for (int j = 0; j < 9 * hd; j++)
            {
                if (!Has(i, j, -2 * hd))
                {
                    Put(i, j, -2 * hd, i, j, -2 * hd);
                }

                if (!Has(i, j, 6 * hd))
                {
                    Put(i, j, 6 * hd, i, j, 6 * hd);
                }
            }
        }

        for (int j = 0; j < 9 * hd; j++)
        {
            for (int k = (int)(-2 * hd); k < 7 * hd; k++)
            {
                if (!Has(0, j, k))
                {
                    Put(0, j, k, 0, j, k);
                }

                if (!Has(8 * hd, j, k))
                {
                    Put(8 * hd, j, k, 8 * hd, j, k);
                }
            }
        }

        for (int i = 0; i < 9 * hd; i++)
        {
            for (int k = (int)(-2 * hd); k < 7 * hd; k++)
            {
                if (!Has(i, 0, k))
                {
                    Put(i, 0, k, i, 0, k);
                }

                if (!Has(i, 8 * hd, k))
                {
                    Put(i, 8 * hd, k, i, 8 * hd, k);
                }
            }
        }

        for (int i = 0; i < 8 * hd; i++)
        {
            for (int j = 0; j < 8 * hd; j++)
            {
                Quad("head", "back", Pt(i, j, -2 * hd), Pt(i + 1, j, -2 * hd),
                    Pt(i + 1, j + 1, -2 * hd), Pt(i, j + 1, -2 * hd),
                    Color((int)((32 * hd - 1) - i), (int)(8 * hd + j)));
                Quad("head", "front", Pt(i, j, 6 * hd), Pt(i + 1, j, 6 * hd),
                    Pt(i + 1, j + 1, 6 * hd), Pt(i, j + 1, 6 * hd),
                    Color((int)(8 * hd + i), (int)(8 * hd + j)));
            }
        }

        for (int j = 0; j < 8 * hd; j++)
        {
            for (int k = (int)(-2 * hd); k < 6 * hd; k++)
            {
                Quad("head", "right", Pt(0, j, k), Pt(0, j, k + 1),
                    Pt(0, j + 1, k + 1), Pt(0, j + 1, k),
                    Color((int)(k + 2 * hd), (int)(8 * hd + j)));
                Quad("head", "left", Pt(8 * hd, j, k), Pt(8 * hd, j, k + 1),
                    Pt(8 * hd, j + 1, k + 1), Pt(8 * hd, j + 1, k),
                    Color((int)((24 * hd - 1) - k - 2 * hd), (int)(8 * hd + j)));
            }
        }

        for (int i = 0; i < 8 * hd; i++)
        {
            for (int k = (int)(-2 * hd); k < 6 * hd; k++)
            {
                Quad("head", "top", Pt(i, 0, k), Pt(i + 1, 0, k),
                    Pt(i + 1, 0, k + 1), Pt(i, 0, k + 1),
                    Color((int)(8 * hd + i), (int)(k + 2 * hd)));
                Quad("head", "bottom", Pt(i, 8 * hd, k), Pt(i + 1, 8 * hd, k),
                    Pt(i + 1, 8 * hd, k + 1), Pt(i, 8 * hd, k + 1),
                    Color((int)(16 * hd + i), (int)(2 * hd + k)));
            }
        }
    }

    // ---------------------------------------------------------------- HELMET

    private void Helmet()
    {
        _volumePoints.Clear();
        double hd = _hdRatio;

        for (int i = 0; i < 9 * hd; i++)
        {
            for (int j = 0; j < 9 * hd; j++)
            {
                if (!Has(i, j, -2 * hd))
                {
                    Put(i, j, -2 * hd, i * 9 / 8.0 - 0.5 * hd, j * 9 / 8.0 - 0.5 * hd,
                        -2.5 * hd);
                }

                if (!Has(i, j, 6 * hd))
                {
                    Put(i, j, 6 * hd, i * 9 / 8.0 - 0.5 * hd, j * 9 / 8.0 - 0.5 * hd,
                        6.5 * hd);
                }
            }
        }

        for (int j = 0; j < 9 * hd; j++)
        {
            for (int k = (int)(-2 * hd); k < 7 * hd; k++)
            {
                if (!Has(0, j, k))
                {
                    Put(0, j, k, -0.5 * hd, j * 9 / 8.0 - 0.5 * hd, k * 9 / 8.0 - 0.5 * hd);
                }

                if (!Has(8 * hd, j, k))
                {
                    Put(8 * hd, j, k, 8.5 * hd, j * 9 / 8.0 - 0.5 * hd,
                        k * 9 / 8.0 - 0.5 * hd);
                }
            }
        }

        for (int i = 0; i < 9 * hd; i++)
        {
            for (int k = (int)(-2 * hd); k < 7 * hd; k++)
            {
                if (!Has(i, 0, k))
                {
                    Put(i, 0, k, i * 9 / 8.0 - 0.5 * hd, -0.5 * hd, k * 9 / 8.0 - 0.5 * hd);
                }

                if (!Has(i, 8 * hd, k))
                {
                    Put(i, 8 * hd, k, i * 9 / 8.0 - 0.5 * hd, 8.5 * hd,
                        k * 9 / 8.0 - 0.5 * hd);
                }
            }
        }

        for (int i = 0; i < 8 * hd; i++)
        {
            for (int j = 0; j < 8 * hd; j++)
            {
                Quad("helmet", "back", Pt(i, j, -2 * hd), Pt(i + 1, j, -2 * hd),
                    Pt(i + 1, j + 1, -2 * hd), Pt(i, j + 1, -2 * hd),
                    Color((int)(32 * hd + (32 * hd - 1) - i), (int)(8 * hd + j)));
                Quad("helmet", "front", Pt(i, j, 6 * hd), Pt(i + 1, j, 6 * hd),
                    Pt(i + 1, j + 1, 6 * hd), Pt(i, j + 1, 6 * hd),
                    Color((int)(32 * hd + 8 * hd + i), (int)(8 * hd + j)));
            }
        }

        for (int j = 0; j < 8 * hd; j++)
        {
            for (int k = (int)(-2 * hd); k < 6 * hd; k++)
            {
                Quad("helmet", "right", Pt(0, j, k), Pt(0, j, k + 1),
                    Pt(0, j + 1, k + 1), Pt(0, j + 1, k),
                    Color((int)(32 * hd + k + 2 * hd), (int)(8 * hd + j)));
                Quad("helmet", "left", Pt(8 * hd, j, k), Pt(8 * hd, j, k + 1),
                    Pt(8 * hd, j + 1, k + 1), Pt(8 * hd, j + 1, k),
                    Color((int)(32 * hd + (24 * hd - 1) - k - 2 * hd), (int)(8 * hd + j)));
            }
        }

        for (int i = 0; i < 8 * hd; i++)
        {
            for (int k = (int)(-2 * hd); k < 6 * hd; k++)
            {
                Quad("helmet", "top", Pt(i, 0, k), Pt(i + 1, 0, k),
                    Pt(i + 1, 0, k + 1), Pt(i, 0, k + 1),
                    Color((int)(32 * hd + 8 * hd + i), (int)(k + 2 * hd)));
                Quad("helmet", "bottom", Pt(i, 8 * hd, k), Pt(i + 1, 8 * hd, k),
                    Pt(i + 1, 8 * hd, k + 1), Pt(i, 8 * hd, k + 1),
                    Color((int)(32 * hd + 16 * hd + i), (int)(2 * hd + k)));
            }
        }
    }

    // ---------------------------------------------------------------- TORSO

    private void Torso()
    {
        _volumePoints.Clear();
        double hd = _hdRatio;

        for (int i = 0; i < 9 * hd; i++)
        {
            for (int j = 0; j < 13 * hd; j++)
            {
                if (!Has(i, j, 0))
                {
                    Put(i, j, 0, i, j + 8 * hd, 0);
                }

                if (!Has(i, j, 4 * hd))
                {
                    Put(i, j, 4 * hd, i, j + 8 * hd, 4 * hd);
                }
            }
        }

        for (int j = 0; j < 13 * hd; j++)
        {
            for (int k = 0; k < 5 * hd; k++)
            {
                if (!Has(0, j, k))
                {
                    Put(0, j, k, 0, j + 8 * hd, k);
                }

                if (!Has(8 * hd, j, k))
                {
                    Put(8 * hd, j, k, 8 * hd, j + 8 * hd, k);
                }
            }
        }

        for (int i = 0; i < 9 * hd; i++)
        {
            for (int k = 0; k < 5 * hd; k++)
            {
                if (!Has(i, 0, k))
                {
                    Put(i, 0, k, i, 0 + 8 * hd, k);
                }

                if (!Has(i, 12 * hd, k))
                {
                    Put(i, 12 * hd, k, i, 12 * hd + 8 * hd, k);
                }
            }
        }

        for (int i = 0; i < 8 * hd; i++)
        {
            for (int j = 0; j < 12 * hd; j++)
            {
                Quad("torso", "back", Pt(i, j, 0), Pt(i + 1, j, 0),
                    Pt(i + 1, j + 1, 0), Pt(i, j + 1, 0),
                    Color((int)((40 * hd - 1) - i), (int)(20 * hd + j)));
                Quad("torso", "front", Pt(i, j, 4 * hd), Pt(i + 1, j, 4 * hd),
                    Pt(i + 1, j + 1, 4 * hd), Pt(i, j + 1, 4 * hd),
                    Color((int)(20 * hd + i), (int)(20 * hd + j)));
            }
        }

        for (int j = 0; j < 12 * hd; j++)
        {
            for (int k = 0; k < 4 * hd; k++)
            {
                Quad("torso", "right", Pt(0, j, k), Pt(0, j, k + 1),
                    Pt(0, j + 1, k + 1), Pt(0, j + 1, k),
                    Color((int)(16 * hd + k), (int)(20 * hd + j)));
                Quad("torso", "left", Pt(8 * hd, j, k), Pt(8 * hd, j, k + 1),
                    Pt(8 * hd, j + 1, k + 1), Pt(8 * hd, j + 1, k),
                    Color((int)((32 * hd - 1) - k), (int)(20 * hd + j)));
            }
        }

        for (int i = 0; i < 8 * hd; i++)
        {
            for (int k = 0; k < 4 * hd; k++)
            {
                Quad("torso", "top", Pt(i, 0, k), Pt(i + 1, 0, k),
                    Pt(i + 1, 0, k + 1), Pt(i, 0, k + 1),
                    Color((int)(20 * hd + i), (int)(16 * hd + k)));
                Quad("torso", "bottom", Pt(i, 12 * hd, k), Pt(i + 1, 12 * hd, k),
                    Pt(i + 1, 12 * hd, k + 1), Pt(i, 12 * hd, k + 1),
                    Color((int)(28 * hd + i), (int)((20 * hd - 1) - k)));
            }
        }
    }

    // ---------------------------------------------------------------- RIGHT ARM

    private void RightArm()
    {
        _volumePoints.Clear();
        double hd = _hdRatio;

        for (int i = 0; i < 9 * hd; i++)
        {
            for (int j = 0; j < 13 * hd; j++)
            {
                if (!Has(i, j, 0))
                {
                    Put(i, j, 0, i - 4 * hd, j + 8 * hd, 0);
                }

                if (!Has(i, j, 4 * hd))
                {
                    Put(i, j, 4 * hd, i - 4 * hd, j + 8 * hd, 4 * hd);
                }
            }
        }

        for (int j = 0; j < 13 * hd; j++)
        {
            for (int k = 0; k < 5 * hd; k++)
            {
                if (!Has(0, j, k))
                {
                    Put(0, j, k, 0 - 4 * hd, j + 8 * hd, k);
                }

                // 上游原样：守卫检查 [8*hd]，写入 [4*hd]，坐标 x = 4*hd - 4*hd = 0
                if (!Has(8 * hd, j, k))
                {
                    Put(4 * hd, j, k, 4 * hd - 4 * hd, j + 8 * hd, k);
                }
            }
        }

        for (int i = 0; i < 9 * hd; i++)
        {
            for (int k = 0; k < 5 * hd; k++)
            {
                if (!Has(i, 0, k))
                {
                    Put(i, 0, k, i - 4 * hd, 0 + 8 * hd, k);
                }

                if (!Has(i, 12 * hd, k))
                {
                    Put(i, 12 * hd, k, i - 4 * hd, 12 * hd + 8 * hd, k);
                }
            }
        }

        if (_isAlex)
        {
            for (int i = 0; i < 3 * hd; i++)
            {
                for (int j = 0; j < 12 * hd; j++)
                {
                    Quad("rightArm", "back", Pt(i, j, 0), Pt(i + 1, j, 0),
                        Pt(i + 1, j + 1, 0), Pt(i, j + 1, 0),
                        Color((int)((51 * hd - 1) - i), (int)(20 * hd + j)));
                    Quad("rightArm", "front", Pt(i, j, 4 * hd), Pt(i + 1, j, 4 * hd),
                        Pt(i + 1, j + 1, 4 * hd), Pt(i, j + 1, 4 * hd),
                        Color((int)(44 * hd + i), (int)(20 * hd + j)));
                }
            }
        }
        else
        {
            for (int i = 0; i < 4 * hd; i++)
            {
                for (int j = 0; j < 12 * hd; j++)
                {
                    Quad("rightArm", "back", Pt(i, j, 0), Pt(i + 1, j, 0),
                        Pt(i + 1, j + 1, 0), Pt(i, j + 1, 0),
                        Color((int)((56 * hd - 1) - i), (int)(20 * hd + j)));
                    Quad("rightArm", "front", Pt(i, j, 4 * hd), Pt(i + 1, j, 4 * hd),
                        Pt(i + 1, j + 1, 4 * hd), Pt(i, j + 1, 4 * hd),
                        Color((int)(44 * hd + i), (int)(20 * hd + j)));
                }
            }
        }

        for (int j = 0; j < 12 * hd; j++)
        {
            for (int k = 0; k < 4 * hd; k++)
            {
                double rightOffsetX = (_isAlex ? 47 : 40) * hd;
                Quad("rightArm", "right", Pt(0, j, k), Pt(0, j, k + 1),
                    Pt(0, j + 1, k + 1), Pt(0, j + 1, k),
                    Color((int)(rightOffsetX + k), (int)(20 * hd + j)));
                double leftOffsetX = (_isAlex ? 40 : 52) * hd;
                Quad("rightArm", "left", Pt(4 * hd, j, k), Pt(4 * hd, j, k + 1),
                    Pt(4 * hd, j + 1, k + 1), Pt(4 * hd, j + 1, k),
                    Color((int)((leftOffsetX - 1) - k), (int)(20 * hd + j)));
            }
        }

        if (_isAlex)
        {
            for (int i = 0; i < 3 * hd; i++)
            {
                for (int k = 0; k < 4 * hd; k++)
                {
                    Quad("rightArm", "top", Pt(i, 0, k), Pt(i + 1, 0, k),
                        Pt(i + 1, 0, k + 1), Pt(i, 0, k + 1),
                        Color((int)(44 * hd + i), (int)(16 * hd + k)));
                    Quad("rightArm", "bottom", Pt(i, 12 * hd, k), Pt(i + 1, 12 * hd, k),
                        Pt(i + 1, 12 * hd, k + 1), Pt(i, 12 * hd, k + 1),
                        Color((int)(47 * hd + i), (int)(16 * hd + k)));
                }
            }
        }
        else
        {
            for (int i = 0; i < 4 * hd; i++)
            {
                for (int k = 0; k < 4 * hd; k++)
                {
                    Quad("rightArm", "top", Pt(i, 0, k), Pt(i + 1, 0, k),
                        Pt(i + 1, 0, k + 1), Pt(i, 0, k + 1),
                        Color((int)(44 * hd + i), (int)(16 * hd + k)));
                    Quad("rightArm", "bottom", Pt(i, 12 * hd, k), Pt(i + 1, 12 * hd, k),
                        Pt(i + 1, 12 * hd, k + 1), Pt(i, 12 * hd, k + 1),
                        Color((int)(48 * hd + i), (int)(16 * hd + k)));
                }
            }
        }
    }

    // ---------------------------------------------------------------- LEFT ARM

    private void LeftArm()
    {
        _volumePoints.Clear();
        double hd = _hdRatio;

        for (int i = 0; i < 9 * hd; i++)
        {
            for (int j = 0; j < 13 * hd; j++)
            {
                if (!Has(i, j, 0))
                {
                    Put(i, j, 0, i + 8 * hd, j + 8 * hd, 0);
                }

                if (!Has(i, j, 4 * hd))
                {
                    Put(i, j, 4 * hd, i + 8 * hd, j + 8 * hd, 4 * hd);
                }
            }
        }

        for (int j = 0; j < 13 * hd; j++)
        {
            for (int k = 0; k < 5 * hd; k++)
            {
                if (!Has(0, j, k))
                {
                    Put(0, j, k, 8 * hd, j + 8 * hd, k);
                }

                // 上游原样：守卫检查 [8*hd]，写入 [4*hd]，坐标 x = 4*hd + 8*hd
                if (!Has(8 * hd, j, k))
                {
                    Put(4 * hd, j, k, 4 * hd + 8 * hd, j + 8 * hd, k);
                }
            }
        }

        for (int i = 0; i < 9 * hd; i++)
        {
            for (int k = 0; k < 5 * hd; k++)
            {
                if (!Has(i, 0, k))
                {
                    Put(i, 0, k, i + 8 * hd, 0 + 8 * hd, k);
                }

                if (!Has(i, 12 * hd, k))
                {
                    Put(i, 12 * hd, k, i + 8 * hd, 12 * hd + 8 * hd, k);
                }
            }
        }

        if (_isAlex)
        {
            for (int i = 0; i < 3 * hd; i++)
            {
                for (int j = 0; j < 12 * hd; j++)
                {
                    Quad("leftArm", "back", Pt(i, j, 0), Pt(i + 1, j, 0),
                        Pt(i + 1, j + 1, 0), Pt(i, j + 1, 0),
                        Color((int)(43 * hd + i), (int)(52 * hd + j)));
                    Quad("leftArm", "front", Pt(i, j, 4 * hd), Pt(i + 1, j, 4 * hd),
                        Pt(i + 1, j + 1, 4 * hd), Pt(i, j + 1, 4 * hd),
                        Color((int)(36 * hd + i), (int)(52 * hd + j)));
                }
            }
        }
        else
        {
            for (int i = 0; i < 4 * hd; i++)
            {
                for (int j = 0; j < 12 * hd; j++)
                {
                    int color1;
                    int color2;

                    if (_isNewSkinType)
                    {
                        color1 = Color((int)(47 * hd - i), (int)(52 * hd + j)); // from right to left
                        color2 = Color((int)(36 * hd + i), (int)(52 * hd + j)); // from left to right
                    }
                    else
                    {
                        color1 = Color((int)((56 * hd - 1) - ((4 * hd - 1) - i)),
                            (int)(20 * hd + j));
                        color2 = Color((int)(44 * hd + ((4 * hd - 1) - i)), (int)(20 * hd + j));
                    }

                    Quad("leftArm", "back", Pt(i, j, 0), Pt(i + 1, j, 0),
                        Pt(i + 1, j + 1, 0), Pt(i, j + 1, 0), color1);
                    Quad("leftArm", "front", Pt(i, j, 4 * hd), Pt(i + 1, j, 4 * hd),
                        Pt(i + 1, j + 1, 4 * hd), Pt(i, j + 1, 4 * hd), color2);
                }
            }
        }

        for (int j = 0; j < 12 * hd; j++)
        {
            for (int k = 0; k < 4 * hd; k++)
            {
                int color1;
                int color2;

                if (_isNewSkinType)
                {
                    color1 = Color((int)(32 * hd + k), (int)(52 * hd + j)); // from left to right
                    color2 = Color((int)(43 * hd - k), (int)(52 * hd + j)); // from right to left
                }
                else
                {
                    color1 = Color((int)(40 * hd + ((4 * hd - 1) - k)), (int)(20 * hd + j));
                    color2 = Color((int)((52 * hd - 1) - ((4 * hd - 1) - k)),
                        (int)(20 * hd + j));
                }

                Quad("leftArm", "right", Pt(0, j, k), Pt(0, j, k + 1),
                    Pt(0, j + 1, k + 1), Pt(0, j + 1, k), color1);
                Quad("leftArm", "left", Pt(4 * hd, j, k), Pt(4 * hd, j, k + 1),
                    Pt(4 * hd, j + 1, k + 1), Pt(4 * hd, j + 1, k), color2);
            }
        }

        if (_isAlex)
        {
            for (int i = 0; i < 3 * hd; i++)
            {
                for (int k = 0; k < 4 * hd; k++)
                {
                    Quad("leftArm", "top", Pt(i, 0, k), Pt(i + 1, 0, k),
                        Pt(i + 1, 0, k + 1), Pt(i, 0, k + 1),
                        Color((int)(36 * hd + i), (int)(48 * hd + k)));
                    Quad("leftArm", "bottom", Pt(i, 12 * hd, k), Pt(i + 1, 12 * hd, k),
                        Pt(i + 1, 12 * hd, k + 1), Pt(i, 12 * hd, k + 1),
                        Color((int)(39 * hd + i), (int)(48 * hd + k)));
                }
            }
        }
        else
        {
            for (int i = 0; i < 4 * hd; i++)
            {
                for (int k = 0; k < 4 * hd; k++)
                {
                    int color1;
                    int color2;

                    if (_isNewSkinType)
                    {
                        color1 = Color((int)(36 * hd + i), (int)(48 * hd + k)); // from left to right
                        color2 = Color((int)(40 * hd + i), (int)(48 * hd + k)); // from left to right
                    }
                    else
                    {
                        color1 = Color((int)(44 * hd + ((4 * hd - 1) - i)), (int)(16 * hd + k));
                        color2 = Color((int)(48 * hd + ((4 * hd - 1) - i)),
                            (int)((20 * hd - 1) - k));
                    }

                    Quad("leftArm", "top", Pt(i, 0, k), Pt(i + 1, 0, k),
                        Pt(i + 1, 0, k + 1), Pt(i, 0, k + 1), color1);
                    Quad("leftArm", "bottom", Pt(i, 12 * hd, k), Pt(i + 1, 12 * hd, k),
                        Pt(i + 1, 12 * hd, k + 1), Pt(i, 12 * hd, k + 1), color2);
                }
            }
        }
    }

    // ---------------------------------------------------------------- RIGHT LEG

    private void RightLeg()
    {
        _volumePoints.Clear();
        double hd = _hdRatio;

        for (int i = 0; i < 9 * hd; i++)
        {
            for (int j = 0; j < 13 * hd; j++)
            {
                if (!Has(i, j, 0))
                {
                    Put(i, j, 0, i, j + 20 * hd, 0);
                }

                if (!Has(i, j, 4 * hd))
                {
                    Put(i, j, 4 * hd, i, j + 20 * hd, 4 * hd);
                }
            }
        }

        for (int j = 0; j < 13 * hd; j++)
        {
            for (int k = 0; k < 5 * hd; k++)
            {
                if (!Has(0, j, k))
                {
                    Put(0, j, k, 0, j + 20 * hd, k);
                }

                // 上游原样：守卫检查 [8*hd]，写入 [4*hd]，坐标 x = 4*hd
                if (!Has(8 * hd, j, k))
                {
                    Put(4 * hd, j, k, 4 * hd, j + 20 * hd, k);
                }
            }
        }

        for (int i = 0; i < 9 * hd; i++)
        {
            for (int k = 0; k < 5 * hd; k++)
            {
                if (!Has(i, 0, k))
                {
                    Put(i, 0, k, i, 0 + 20 * hd, k);
                }

                if (!Has(i, 12 * hd, k))
                {
                    Put(i, 12 * hd, k, i, 12 * hd + 20 * hd, k);
                }
            }
        }

        for (int i = 0; i < 4 * hd; i++)
        {
            for (int j = 0; j < 12 * hd; j++)
            {
                Quad("rightLeg", "back", Pt(i, j, 0), Pt(i + 1, j, 0),
                    Pt(i + 1, j + 1, 0), Pt(i, j + 1, 0),
                    Color((int)((16 * hd - 1) - i), (int)(20 * hd + j)));
                Quad("rightLeg", "front", Pt(i, j, 4 * hd), Pt(i + 1, j, 4 * hd),
                    Pt(i + 1, j + 1, 4 * hd), Pt(i, j + 1, 4 * hd),
                    Color((int)(4 * hd + i), (int)(20 * hd + j)));
            }
        }

        for (int j = 0; j < 12 * hd; j++)
        {
            for (int k = 0; k < 4 * hd; k++)
            {
                Quad("rightLeg", "right", Pt(0, j, k), Pt(0, j, k + 1),
                    Pt(0, j + 1, k + 1), Pt(0, j + 1, k),
                    Color((int)(0 + k), (int)(20 * hd + j)));
                Quad("rightLeg", "left", Pt(4 * hd, j, k), Pt(4 * hd, j, k + 1),
                    Pt(4 * hd, j + 1, k + 1), Pt(4 * hd, j + 1, k),
                    Color((int)((12 * hd - 1) - k), (int)(20 * hd + j)));
            }
        }

        for (int i = 0; i < 4 * hd; i++)
        {
            for (int k = 0; k < 4 * hd; k++)
            {
                Quad("rightLeg", "top", Pt(i, 0, k), Pt(i + 1, 0, k),
                    Pt(i + 1, 0, k + 1), Pt(i, 0, k + 1),
                    Color((int)(4 * hd + i), (int)(16 * hd + k)));
                Quad("rightLeg", "bottom", Pt(i, 12 * hd, k), Pt(i + 1, 12 * hd, k),
                    Pt(i + 1, 12 * hd, k + 1), Pt(i, 12 * hd, k + 1),
                    Color((int)(8 * hd + i), (int)(16 * hd + k)));
            }
        }
    }

    // ---------------------------------------------------------------- LEFT LEG

    private void LeftLeg()
    {
        _volumePoints.Clear();
        double hd = _hdRatio;

        for (int i = 0; i < 9 * hd; i++)
        {
            for (int j = 0; j < 13 * hd; j++)
            {
                if (!Has(i, j, 0))
                {
                    Put(i, j, 0, i + 4 * hd, j + 20 * hd, 0);
                }

                if (!Has(i, j, 4 * hd))
                {
                    Put(i, j, 4 * hd, i + 4 * hd, j + 20 * hd, 4 * hd);
                }
            }
        }

        for (int j = 0; j < 13 * hd; j++)
        {
            for (int k = 0; k < 5 * hd; k++)
            {
                if (!Has(0, j, k))
                {
                    Put(0, j, k, 0 + 4 * hd, j + 20 * hd, k);
                }

                // 上游原样：守卫检查 [8*hd]，写入 [4*hd]，坐标 x = 4*hd + 4*hd
                if (!Has(8 * hd, j, k))
                {
                    Put(4 * hd, j, k, 4 * hd + 4 * hd, j + 20 * hd, k);
                }
            }
        }

        for (int i = 0; i < 9 * hd; i++)
        {
            for (int k = 0; k < 5 * hd; k++)
            {
                if (!Has(i, 0, k))
                {
                    Put(i, 0, k, i + 4 * hd, 0 + 20 * hd, k);
                }

                if (!Has(i, 12 * hd, k))
                {
                    Put(i, 12 * hd, k, i + 4 * hd, 12 * hd + 20 * hd, k);
                }
            }
        }

        for (int i = 0; i < 4 * hd; i++)
        {
            for (int j = 0; j < 12 * hd; j++)
            {
                int color1;
                int color2;

                if (_isNewSkinType)
                {
                    color1 = Color((int)(31 * hd - i), (int)(52 * hd + j)); // from right to left
                    color2 = Color((int)(20 * hd + i), (int)(52 * hd + j)); // from left to right
                }
                else
                {
                    color1 = Color((int)((16 * hd - 1) - ((4 * hd - 1) - i)),
                        (int)(20 * hd + j));
                    color2 = Color((int)(4 * hd + ((4 * hd - 1) - i)), (int)(20 * hd + j));
                }

                Quad("leftLeg", "back", Pt(i, j, 0), Pt(i + 1, j, 0),
                    Pt(i + 1, j + 1, 0), Pt(i, j + 1, 0), color1);
                Quad("leftLeg", "front", Pt(i, j, 4 * hd), Pt(i + 1, j, 4 * hd),
                    Pt(i + 1, j + 1, 4 * hd), Pt(i, j + 1, 4 * hd), color2);
            }
        }

        for (int j = 0; j < 12 * hd; j++)
        {
            for (int k = 0; k < 4 * hd; k++)
            {
                int color1;
                int color2;

                if (_isNewSkinType)
                {
                    color1 = Color((int)(16 * hd + k), (int)(52 * hd + j)); // from left to right
                    color2 = Color((int)(27 * hd - k), (int)(52 * hd + j)); // from right to left
                }
                else
                {
                    color1 = Color((int)(0 + ((4 * hd - 1) - k)), (int)(20 * hd + j));
                    color2 = Color((int)((12 * hd - 1) - ((4 * hd - 1) - k)),
                        (int)(20 * hd + j));
                }

                Quad("leftLeg", "right", Pt(0, j, k), Pt(0, j, k + 1),
                    Pt(0, j + 1, k + 1), Pt(0, j + 1, k), color1);
                Quad("leftLeg", "left", Pt(4 * hd, j, k), Pt(4 * hd, j, k + 1),
                    Pt(4 * hd, j + 1, k + 1), Pt(4 * hd, j + 1, k), color2);
            }
        }

        for (int i = 0; i < 4 * hd; i++)
        {
            for (int k = 0; k < 4 * hd; k++)
            {
                int color1;
                int color2;

                if (_isNewSkinType)
                {
                    color1 = Color((int)(20 * hd + i), (int)(48 * hd + k)); // from left to right
                    color2 = Color((int)(24 * hd + i), (int)(48 * hd + k)); // from left to right
                }
                else
                {
                    color1 = Color((int)(4 * hd + ((4 * hd - 1) - i)), (int)(16 * hd + k));
                    color2 = Color((int)(8 * hd + ((4 * hd - 1) - i)),
                        (int)((20 * hd - 1) - k));
                }

                Quad("leftLeg", "top", Pt(i, 0, k), Pt(i + 1, 0, k),
                    Pt(i + 1, 0, k + 1), Pt(i, 0, k + 1), color1);
                Quad("leftLeg", "bottom", Pt(i, 12 * hd, k), Pt(i + 1, 12 * hd, k),
                    Pt(i + 1, 12 * hd, k + 1), Pt(i, 12 * hd, k + 1), color2);
            }
        }
    }

    // ======================================================== rotation & projection

    private void MemberRotation()
    {
        RotatePart("head", 4, 8, 2);
        RotatePart("helmet", 4, 8, 2);

        if (!_headOnly)
        {
            RotatePart("rightArm", -2, 8, 2);
            RotatePart("leftArm", 10, 8, 2);
            // 腿部 z 偏移：sinAlpha < 0 ? 0 : 4（上游条件保留）
            Angles rl = _membersAngles["rightLeg"];
            RotatePart("rightLeg", 2, 20, rl.SinAlpha < 0 ? 0 : 4);
            Angles ll = _membersAngles["leftLeg"];
            RotatePart("leftLeg", 6, 20, ll.SinAlpha < 0 ? 0 : 4);
        }
    }

    private void RotatePart(string part, double dx, double dy, double dz)
    {
        Angles m = _membersAngles[part];

        foreach (List<FacePolygon> facePolys in _polygons[part].Values)
        {
            foreach (FacePolygon poly in facePolys)
            {
                poly.PreProject(dx, dy, dz, m.CosAlpha, m.SinAlpha, m.CosOmega, m.SinOmega);
            }
        }
    }

    private void CreateProjectionPlan()
    {
        double ca = _cosAlpha;
        double sa = _sinAlpha;
        double co = _cosOmega;
        double so = _sinOmega;

        foreach (Dictionary<string, List<FacePolygon>> piece in _polygons.Values)
        {
            foreach (List<FacePolygon> facePolys in piece.Values)
            {
                foreach (FacePolygon poly in facePolys)
                {
                    if (!poly.Projected)
                    {
                        poly.Project(ca, sa, co, so, _bounds);
                    }
                }
            }
        }
    }

    private Canvas DisplayImage()
    {
        double minX = _bounds.MinX;
        double minY = _bounds.MinY;
        double width = _bounds.MaxX - _bounds.MinX;
        double height = _bounds.MaxY - _bounds.MinY;
        double ratio2 = _ratio * 2;

        double srcWidthD = ratio2 * width + 1;
        double srcHeightD = ratio2 * height + 1;
        int srcWidth = (int)srcWidthD;
        int srcHeight = (int)srcHeightD;

        Canvas image = Canvas.CreateEmpty(srcWidth, srcHeight);

        foreach (DrawPass pass in GetDisplayOrder())
        {
            foreach (string face in pass.Faces)
            {
                foreach (FacePolygon poly in _polygons[pass.Piece][face])
                {
                    poly.Draw(image, minX, minY, ratio2);
                }
            }
        }

        // Anti-aliasing
        double realWidthD = srcWidthD / 2;
        double realHeightD = srcHeightD / 2;
        int realWidth = (int)realWidthD;
        int realHeight = (int)realHeightD;
        Canvas destImage = Canvas.CreateEmpty(realWidth, realHeight);
        destImage.ResampleRegion(image, 0, 0, realWidth, realHeight, 0, 0,
            image.Width, image.Height);

        return destImage;
    }

    private IReadOnlyList<string> VisibleFront(string part) => _visibleFaces[part].Front;

    /// <summary><c>getDisplayOrder()</c>（分支与顺序逐条对齐源码）。</summary>
    private List<DrawPass> GetDisplayOrder()
    {
        var displayOrder = new List<DrawPass>();

        if (_frontFaces.Contains("top"))
        {
            if (_frontFaces.Contains("right"))
            {
                displayOrder.Add(new DrawPass("leftLeg", _backFaces));
                displayOrder.Add(new DrawPass("leftLeg", VisibleFront("leftLeg")));
                displayOrder.Add(new DrawPass("rightLeg", _backFaces));
                displayOrder.Add(new DrawPass("rightLeg", VisibleFront("rightLeg")));
                displayOrder.Add(new DrawPass("leftArm", _backFaces));
                displayOrder.Add(new DrawPass("leftArm", VisibleFront("leftArm")));
                displayOrder.Add(new DrawPass("torso", _backFaces));
                displayOrder.Add(new DrawPass("torso", VisibleFront("torso")));
                displayOrder.Add(new DrawPass("rightArm", _backFaces));
                displayOrder.Add(new DrawPass("rightArm", VisibleFront("rightArm")));
            }
            else
            {
                displayOrder.Add(new DrawPass("rightLeg", _backFaces));
                displayOrder.Add(new DrawPass("rightLeg", VisibleFront("rightLeg")));
                displayOrder.Add(new DrawPass("leftLeg", _backFaces));
                displayOrder.Add(new DrawPass("leftLeg", VisibleFront("leftLeg")));
                displayOrder.Add(new DrawPass("rightArm", _backFaces));
                displayOrder.Add(new DrawPass("rightArm", VisibleFront("rightArm")));
                displayOrder.Add(new DrawPass("torso", _backFaces));
                displayOrder.Add(new DrawPass("torso", VisibleFront("torso")));
                displayOrder.Add(new DrawPass("leftArm", _backFaces));
                displayOrder.Add(new DrawPass("leftArm", VisibleFront("leftArm")));
            }

            displayOrder.Add(new DrawPass("helmet", _backFaces));
            displayOrder.Add(new DrawPass("head", _backFaces));
            displayOrder.Add(new DrawPass("head", VisibleFront("head")));
            displayOrder.Add(new DrawPass("helmet", VisibleFront("head")));
        }
        else
        {
            displayOrder.Add(new DrawPass("helmet", _backFaces));
            displayOrder.Add(new DrawPass("head", _backFaces));
            displayOrder.Add(new DrawPass("head", VisibleFront("head")));
            displayOrder.Add(new DrawPass("helmet", VisibleFront("head")));

            if (_frontFaces.Contains("right"))
            {
                displayOrder.Add(new DrawPass("leftArm", _backFaces));
                displayOrder.Add(new DrawPass("leftArm", VisibleFront("leftArm")));
                displayOrder.Add(new DrawPass("torso", _backFaces));
                displayOrder.Add(new DrawPass("torso", VisibleFront("torso")));
                displayOrder.Add(new DrawPass("rightArm", _backFaces));
                displayOrder.Add(new DrawPass("rightArm", VisibleFront("rightArm")));
                displayOrder.Add(new DrawPass("leftLeg", _backFaces));
                displayOrder.Add(new DrawPass("leftLeg", VisibleFront("leftLeg")));
                displayOrder.Add(new DrawPass("rightLeg", _backFaces));
                displayOrder.Add(new DrawPass("rightLeg", VisibleFront("rightLeg")));
            }
            else
            {
                displayOrder.Add(new DrawPass("rightArm", _backFaces));
                displayOrder.Add(new DrawPass("rightArm", VisibleFront("rightArm")));
                displayOrder.Add(new DrawPass("torso", _backFaces));
                displayOrder.Add(new DrawPass("torso", VisibleFront("torso")));
                displayOrder.Add(new DrawPass("leftArm", _backFaces));
                displayOrder.Add(new DrawPass("leftArm", VisibleFront("leftArm")));
                displayOrder.Add(new DrawPass("rightLeg", _backFaces));
                displayOrder.Add(new DrawPass("rightLeg", VisibleFront("rightLeg")));
                displayOrder.Add(new DrawPass("leftLeg", _backFaces));
                displayOrder.Add(new DrawPass("leftLeg", VisibleFront("leftLeg")));
            }
        }

        return displayOrder;
    }
}

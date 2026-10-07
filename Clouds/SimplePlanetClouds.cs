using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

/// <summary>
/// Облака планеты из двух слоёв, которые плавно сменяют друг друга по высоте камеры:
///  - у земли — Volumetric Clouds HDRP: объёмные облака, сквозь них можно пролететь;
///  - выше — сфера вокруг планеты с текстурой облаков всей планеты.
///
/// Чтобы при смене слоёв рисунок не прыгал, скрипт вырезает из текстуры планеты квадрат
/// вокруг точки старта (патч) и отдаёт его объёмным облакам как карту покрытия (Cumulus Map).
/// Масштаб и ориентация патча считаются по формулам самого HDRP, калибровать ничего не нужно.
///
/// Сферу рисует шейдер CloudShellHDRP.hlsl. Он считает края облаков той же формулой и тем же
/// шумом формы и эрозии, что и HDRP. Настройки этого шума скрипт каждый кадр переносит
/// из Volumetric Clouds в материал сферы.
///
/// Отладка: панель в левом верхнем углу — высота, переход, мини-карта патча с камерой,
/// направлением взгляда и горизонтом, и карта планеты с местом патча.
/// </summary>
public class SimplePlanetClouds : MonoBehaviour
{
    // ==================================================================
    // Настройки

    [Header("Ссылки")]
    [Tooltip("Камера игрока (объект с компонентом Camera). От её высоты над землёй зависит, " +
             "какой слой облаков виден.")]
    public Transform player;

    [Tooltip("Volume, в профиле которого есть Volumetric Clouds (обычно Sky and Fog Volume). " +
             "Скрипт меняет копию профиля только на время игры, сам ассет не портится.")]
    public Volume skyVolume;

    [Tooltip("Материал облачной сферы: Shader Graph с CloudShellHDRP.hlsl. " +
             "В Base Color Map — текстура облаков всей планеты, облачность в альфа-канале.")]
    public Material shellMaterial;

    [Header("Планета")]
    [Tooltip("Радиус планеты, км. Должен совпадать с Planet Radius в Visual Environment " +
             "(в HDRP по умолчанию 6378.1).")]
    public float planetRadiusKm = 6378.1f;

    [Tooltip("На какой высоте над землёй висит сфера с облаками, км.")]
    public float shellAltitudeKm = 3f;

    [Header("Переход между слоями")]
    [Tooltip("С этой высоты камеры, км, объёмные облака начинают гаснуть, а сфера — проявляться.")]
    public float fadeStartKm = 30f;

    [Tooltip("С этой высоты, км, видна только сфера, объёмные облака выключены.")]
    public float fadeEndKm = 70f;

    [Header("Карта облаков")]
    [Tooltip("Широта места на карте облаков, которое окажется прямо над началом координат сцены.")]
    [Range(-80f, 80f)] public float startLatitude = 45f;

    [Tooltip("Долгота этого места. 0 — середина картинки по горизонтали.")]
    [Range(-180f, 180f)] public float startLongitude = -30f;

    [Tooltip("Читаемая копия карты облаков (Read/Write включён, 2048–4096 хватает), из неё режется патч. " +
             "Тогда у основной текстуры сферы Read/Write можно выключить и поставить 8k. " +
             "Пусто — берётся Base Color Map из материала сферы.")]
    public Texture2D patchSourceTexture;

    [Tooltip("Сторона патча, км: на этом квадрате вокруг старта объёмные облака повторяют карту. " +
             "Дальше HDRP повторяет патч по кругу. Больше — шире совпадение, но крупнее пиксели: " +
             "в патче всего 256×256 пикселей.")]
    public float patchSizeKm = 1000f;

    [Tooltip("Альфа карты ниже этого значения — ясное небо.")]
    [Range(0f, 0.9f)] public float coverageThreshold = 0.2f;

    [Tooltip("Множитель покрытия после порога: больше 1 — облаков больше. " +
             "Заменяет Cumulus Map Multiplier в Volume (тот скрипт держит равным 1).")]
    [Range(0f, 3f)] public float coverageMultiplier = 1f;

    [Tooltip("Как полупрозрачные места карты превращаются в облака. HDRP возводит покрытие в квадрат, " +
             "и без поправки полупрозрачное почти исчезает. 0.35–0.5 — доля неба под облаками примерно " +
             "равна альфе карты; 1 — без поправки. Действует одинаково на объёмные облака и сферу.")]
    [Range(0.2f, 1f)] public float coverageGamma = 0.4f;

    [Header("Сфера")]
    [Tooltip("Цвет сферы. Если сфера светлее или темнее объёмных облаков — подберите здесь.")]
    public Color shellTint = Color.white;

    [Tooltip("Плотность облаков на сфере: 1 — как у объёмных. Делает гуще сами облака, " +
             "но не заполняет промежутки между ними.")]
    [Range(0.1f, 4f)] public float shellOpacity = 1f;

    [Tooltip("С какого расстояния от камеры, км, мелкие детали на сфере начинают гаснуть.")]
    public float detailFadeStartKm = 150f;

    [Tooltip("С какого расстояния, км, на сфере остаётся только карта облаков без деталей.")]
    public float detailFadeEndKm = 800f;

    [Tooltip("3D-шум формы облаков из HDRP (WorleyNoise128RGBA). В редакторе подставляется сам.")]
    public Texture3D hdrpShapeNoise;

    [Tooltip("3D-шум эрозии HDRP «Worley 32» (WorleyNoise32RGB). В редакторе подставляется сам.")]
    public Texture3D hdrpWorleyErosion;

    [Tooltip("3D-шум эрозии HDRP «Perlin 32» (PerlinNoise32RGB). В редакторе подставляется сам.")]
    public Texture3D hdrpPerlinErosion;

    [Header("Отладка")]
    [Tooltip("Панель в левом верхнем углу: высота, переход, мини-карта патча с камерой, " +
             "направлением взгляда и горизонтом.")]
    public bool showDebug = true;

    // ==================================================================
    // Внутреннее состояние

    // Константа из формулы HDRP (ComputeNormalizationFactor). Это не радиус вашей планеты.
    const float HdrpEarthRadiusM = 6378100f;

    // Больше HDRP не принимает (Cloud Map Resolution = Ultra 256×256).
    const int PatchResolution = 256;

    bool _ready;                 // все ссылки на месте, можно работать
    VolumetricClouds _clouds;    // Volumetric Clouds из копии профиля skyVolume
    float _groundDensity;        // Density Multiplier из профиля — плотность облаков у земли
    Camera _cam;
    float _baseFarClip;          // Far Clip камеры до нас, вернём при выключении
    float _farNeeded;            // какой Far Clip нужен, чтобы сфера была видна до горизонта

    GameObject _shell;
    Material _shellMat;          // копия shellMaterial, чтобы не менять ассет
    bool _shellHasDetail;        // в материале есть параметры CloudShellHDRP
    Texture2D _patch;
    Texture _patchSource;        // карта, из которой вырезан патч (для превью в панели)
    string _patchKey;            // настройки, с которыми собран патч; изменились — пересобираем
    string _patchError;          // почему патч не собрался

    float _altitudeKm;           // высота камеры над землёй
    float _horizonKm;            // расстояние до горизонта с этой высоты
    float _transition;           // 0 — только объёмные облака, 1 — только сфера

    readonly List<string> _problems = new List<string>();

    Vector3 PlanetCenter => new Vector3(0f, -planetRadiusKm * 1000f, 0f);
    float PatchSizeM => patchSizeKm * 1000f;

    // ==================================================================
    // Запуск

    void Start()
    {
        if (player == null || skyVolume == null || shellMaterial == null)
        {
            Problem("не заданы player, skyVolume или shellMaterial — скрипт не работает");
            return;
        }

        if (!skyVolume.profile.TryGet(out _clouds))   // .profile — копия на время игры
        {
            Problem("в профиле skyVolume нет Volumetric Clouds — скрипт не работает");
            return;
        }

        _groundDensity = _clouds.densityMultiplier.value;
        if (_groundDensity < 0.01f)
            Problem("Density Multiplier в Volumetric Clouds равен 0 — объёмных облаков не будет видно");

        _cam = player.GetComponent<Camera>();
        if (_cam == null) _cam = Camera.main;
        if (_cam == null) Problem("камера не найдена — Far Clip не подстраивается, сфера может обрезаться");
        else _baseFarClip = _cam.farClipPlane;

        CheckVisualEnvironment();
        CreateShell();
        SetupVolumetricClouds();
        _ready = true;
    }

    void OnEnable()
    {
        RenderPipelineManager.beginCameraRendering += OnBeginCameraRendering;
    }

    void OnDisable()
    {
        RenderPipelineManager.beginCameraRendering -= OnBeginCameraRendering;
        if (_cam != null && _baseFarClip > 0f) _cam.farClipPlane = _baseFarClip;
    }

    void OnDestroy()
    {
        if (_shell != null) Destroy(_shell);
        if (_shellMat != null) Destroy(_shellMat);
        if (_patch != null) Destroy(_patch);
    }

    /// <summary>Запоминает проблему: она видна в панели отладки и в консоли.</summary>
    void Problem(string message)
    {
        _problems.Add(message);
        Debug.LogWarning("[Clouds] " + message, this);
    }

    /// <summary>
    /// Проверяет Visual Environment, если он лежит в том же Volume. Схема работает, только когда
    /// планета в мировых координатах с центром в (0, -радиус, 0) и радиус совпадает с planetRadiusKm.
    /// </summary>
    void CheckVisualEnvironment()
    {
        if (!skyVolume.profile.TryGet(out VisualEnvironment env)) return;   // он в другом Volume — не проверяем

        if (Mathf.Abs(env.planetRadius.value - planetRadiusKm) > 0.5f)
            Problem($"Planet Radius в Visual Environment = {env.planetRadius.value:F1} км, " +
                    $"а planetRadiusKm = {planetRadiusKm:F1} — поставьте одинаковые");
        if (env.renderingSpace.value != RenderingSpace.World)
            Problem("в Visual Environment Rendering Space должен быть World");
        if (env.centerMode.value != VisualEnvironment.PlanetMode.Automatic)
            Problem("в Visual Environment Center Mode должен быть Automatic");
    }

    /// <summary>
    /// Создаёт сферу облаков: меш-сферу с развёрткой как у карты (долгота по U, широта по V),
    /// копию материала и поворот, который ставит точку старта на вершину планеты.
    /// </summary>
    void CreateShell()
    {
        _shellMat = new Material(shellMaterial);
        if (_shellMat.HasProperty("_SurfaceType") && _shellMat.GetFloat("_SurfaceType") < 0.5f)
            Problem("материал сферы Opaque — нужен Surface Type = Transparent");

        _shellHasDetail = _shellMat.HasProperty("_CloudShape");
        if (!_shellHasDetail)
            Problem("в материале сферы нет _CloudShape — граф без CloudShellHDRP, деталей на сфере не будет");
        else if (hdrpShapeNoise == null || hdrpWorleyErosion == null || hdrpPerlinErosion == null)
            Problem("не заданы 3D-шумы HDRP (hdrpShapeNoise, hdrpWorleyErosion, hdrpPerlinErosion)");

        _shell = new GameObject("Cloud Shell");
        _shell.AddComponent<MeshFilter>().mesh = MakeSphere(256);
        var renderer = _shell.AddComponent<MeshRenderer>();
        renderer.sharedMaterial = _shellMat;
        renderer.shadowCastingMode = ShadowCastingMode.Off;
        _shell.transform.SetPositionAndRotation(PlanetCenter, MapRotation);
    }

    /// <summary>
    /// Переводит Volumetric Clouds в режим, где карту покрытия задаём мы: Advanced, карта 256×256,
    /// только Cumulus Map (остальные карты убраны), без сдвига карты.
    /// Шейдер сферы рассчитан именно на такой режим.
    /// </summary>
    void SetupVolumetricClouds()
    {
        _clouds.cloudControl.Override(VolumetricClouds.CloudControl.Advanced);
        _clouds.cloudMapResolution.Override(VolumetricClouds.CloudMapResolution.Ultra256x256);
        _clouds.cloudOffset.Override(Vector2.zero);
        _clouds.altoStratusMap.Override(null);
        _clouds.cumulonimbusMap.Override(null);
        _clouds.rainMap.Override(null);

        if (!Mathf.Approximately(_clouds.cumulusMapMultiplier.value, 1f))
            Debug.Log($"[Clouds] Cumulus Map Multiplier {_clouds.cumulusMapMultiplier.value:F2} заменён на 1, " +
                      "вместо него используйте coverageMultiplier", this);
        _clouds.cumulusMapMultiplier.Override(1f);

        RebuildPatchIfNeeded();
    }

    // ==================================================================
    // Каждый кадр

    void Update()
    {
        if (!_ready) return;

        // высота над землёй и расстояние до горизонта
        _altitudeKm = Vector3.Distance(player.position, PlanetCenter) / 1000f - planetRadiusKm;
        double r = planetRadiusKm, h = r + System.Math.Max(_altitudeKm, 0f);
        _horizonKm = (float)System.Math.Sqrt(h * h - r * r);

        // 0 ниже fadeStartKm, 1 выше fadeEndKm, плавно между ними
        _transition = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(fadeStartKm, fadeEndKm, _altitudeKm));

        RebuildPatchIfNeeded();
        UpdateVolumetricClouds();
        UpdateShell();
        UpdateFarClip();
    }

    /// <summary>
    /// Объёмные облака гаснут по мере перехода и выключаются совсем, когда видна только сфера.
    /// Cloud Tiling ставится так, чтобы патч занял ровно patchSizeKm (зависит от высоты слоя,
    /// поэтому пересчитывается каждый кадр — вдруг её поменяли в Volume).
    /// </summary>
    void UpdateVolumetricClouds()
    {
        _clouds.densityMultiplier.Override(_groundDensity * (1f - _transition));
        _clouds.enable.Override(_transition < 0.999f);

        float tiling = HdrpCloudMapSizeM() / PatchSizeM;
        _clouds.cloudTiling.Override(new Vector2(tiling, tiling));
    }

    /// <summary>
    /// Сфера проявляется по мере перехода (альфа в _BaseColor) и прячется, пока полностью прозрачна.
    /// Радиус пересчитывается, чтобы shellAltitudeKm можно было менять прямо в игре.
    /// </summary>
    void UpdateShell()
    {
        Color tint = shellTint;
        tint.a = _transition;
        _shellMat.SetColor("_BaseColor", tint);
        _shell.transform.localScale = Vector3.one * (planetRadiusKm + shellAltitudeKm) * 1000f;
        _shell.SetActive(_transition > 0.001f);

        UpdateShellDetail();
    }

    /// <summary>
    /// Сколько нужно Far Clip, чтобы сфера была видна до самого горизонта:
    /// расстояние до касательной к сфере облаков плюс запас.
    /// Само значение ставится перед рендером в OnBeginCameraRendering.
    /// </summary>
    void UpdateFarClip()
    {
        if (_cam == null) return;

        double camDist = Vector3.Distance(_cam.transform.position, PlanetCenter);
        double shellRadius = (planetRadiusKm + shellAltitudeKm) * 1000.0;
        double tangent = camDist > shellRadius
            ? System.Math.Sqrt(camDist * camDist - shellRadius * shellRadius)
            : 0.0;

        _farNeeded = Mathf.Max(_baseFarClip, (float)(tangent * 1.1 + 20000.0));
    }

    /// <summary>
    /// Ставит Far Clip прямо перед рендером камеры — после всех Update и LateUpdate,
    /// так что его не перебьёт никто (например, Cinemachine).
    /// </summary>
    void OnBeginCameraRendering(ScriptableRenderContext context, Camera cam)
    {
        if (cam == _cam && _farNeeded > 0f) cam.farClipPlane = _farNeeded;
    }

    // ==================================================================
    // Патч: кусок карты облаков для объёмных облаков

    /// <summary>
    /// Сколько метров мира занимает одна карта облаков HDRP при Cloud Tiling = 1.
    /// Формула скопирована из HDRP (ComputeNormalizationFactor): зависит от радиуса планеты
    /// и средней высоты облачного слоя.
    /// </summary>
    float HdrpCloudMapSizeM()
    {
        double mid = _clouds.bottomAltitude.value + _clouds.altitudeRange.value * 0.5;
        double k = HdrpEarthRadiusM + mid;
        return (float)System.Math.Sqrt(System.Math.Max(k * k - HdrpEarthRadiusM * planetRadiusKm * 1000.0, 1.0));
    }

    /// <summary>
    /// Пиксель патча (u, v от 0 до 1) → точка мира (x, z), метры. Так HDRP читает Cumulus Map:
    /// X мира идёт вдоль U, а Z мира — против V (HDRP переворачивает карту по вертикали,
    /// tapUV = (x, 1 - y) в CloudMapGenerator.compute). Центр патча — над началом координат.
    /// </summary>
    Vector2 PatchPixelToWorldXZ(float u, float v) =>
        new Vector2((u - 0.5f) * PatchSizeM, (0.5f - v) * PatchSizeM);

    /// <summary>
    /// Альфа карты → покрытие облаками 0..1: порог, множитель и степень.
    /// Шейдер сферы считает ровно так же (CloudShellHDRP.hlsl), поэтому слои совпадают.
    /// </summary>
    float Coverage(float alpha) =>
        Mathf.Pow(Mathf.Clamp01((alpha - coverageThreshold) / (1f - coverageThreshold) * coverageMultiplier),
                  coverageGamma);

    /// <summary>Пересобирает патч, если изменились настройки, от которых он зависит.</summary>
    void RebuildPatchIfNeeded()
    {
        string key = $"{startLatitude}|{startLongitude}|{patchSizeKm}|{coverageThreshold}|{coverageMultiplier}|" +
                     $"{coverageGamma}|{planetRadiusKm}|{shellAltitudeKm}|{patchSourceTexture}";
        if (key == _patchKey) return;
        _patchKey = key;

        _shell.transform.rotation = MapRotation;   // широта и долгота старта могли поменяться
        _patchError = BuildPatch();
        if (_patchError != null) Debug.LogWarning("[Clouds] " + _patchError, this);
    }

    /// <summary>
    /// Вырезает из карты облаков квадрат patchSizeKm × patchSizeKm вокруг старта и отдаёт его
    /// объёмным облакам как Cumulus Map. Для каждого пикселя патча: где он лежит в мире,
    /// какая точка сферы облаков над этим местом и что нарисовано на карте в этой точке.
    /// Возвращает текст ошибки или null, если всё получилось.
    /// </summary>
    string BuildPatch()
    {
        _patchSource = patchSourceTexture != null ? patchSourceTexture : shellMaterial.GetTexture("_BaseColorMap");
        var source = _patchSource as Texture2D;
        if (source == null)
            return "нет карты облаков: задайте patchSourceTexture или Base Color Map в материале сферы";
        if (!source.isReadable)
            return $"у '{source.name}' выключен Read/Write — включите или задайте читаемую копию в patchSourceTexture";

        float shellRadius = (planetRadiusKm + shellAltitudeKm) * 1000f;
        Quaternion worldToMap = Quaternion.Inverse(MapRotation);
        var pixels = new Color32[PatchResolution * PatchResolution];
        float cloudy = 0f;

        for (int j = 0; j < PatchResolution; j++)
            for (int i = 0; i < PatchResolution; i++)
            {
                Vector2 xz = PatchPixelToWorldXZ((i + 0.5f) / PatchResolution, (j + 0.5f) / PatchResolution);

                // точка сферы облаков прямо над этим местом мира
                float y = Mathf.Sqrt(Mathf.Max(shellRadius * shellRadius - xz.x * xz.x - xz.y * xz.y, 0f));
                Vector2 uv = DirToUV(worldToMap * new Vector3(xz.x, y, xz.y).normalized);

                float coverage = Coverage(source.GetPixelBilinear(uv.x, uv.y).a);
                cloudy += coverage;
                byte c = (byte)(coverage * 255f);
                pixels[j * PatchResolution + i] = new Color32(c, c, c, 255);
            }

        if (_patch == null)
            _patch = new Texture2D(PatchResolution, PatchResolution, TextureFormat.RGBA32, false, true)
            {
                name = "CloudPatch",
                wrapMode = TextureWrapMode.Repeat
            };
        _patch.SetPixels32(pixels);
        _patch.Apply(false);
        _clouds.cumulusMap.Override(_patch);

        return cloudy <= 0f
            ? "патч пустой: на карте здесь нет облаков. Уменьшите coverageThreshold или смените широту/долготу"
            : null;
    }

    // ==================================================================
    // Сфера: та же форма и эрозия, что у объёмных облаков

    /// <summary>
    /// Передаёт в материал сферы настройки формы и эрозии из Volumetric Clouds, чтобы шейдер
    /// считал края облаков той же формулой, что и HDRP. Что лежит в каждом векторе — см. CloudShellHDRP.hlsl.
    /// </summary>
    void UpdateShellDetail()
    {
        if (!_shellHasDetail) return;

        // слой облаков: центр планеты и радиус нижней границы слоя
        Vector3 c = PlanetCenter;
        _shellMat.SetVector("_CloudLayer", new Vector4(c.x, c.y, c.z,
                                                       planetRadiusKm * 1000f + _clouds.bottomAltitude.value));

        // форма: Shape Scale, Shape Factor, Shape Offset
        Vector3 offset = _clouds.shapeOffset.value;
        _shellMat.SetVector("_CloudShape", new Vector4(_clouds.shapeScale.value, _clouds.shapeFactor.value,
                                                       offset.x, offset.z));

        // эрозия: текстура и поправка по типу шума — как в HDRP
        bool perlin = _clouds.erosionNoiseType.value == VolumetricClouds.CloudErosionNoise.Perlin32;
        float compensation = perlin ? 0.75f : 1f;
        float micro = _clouds.microErosion.value ? _clouds.microErosionFactor.value * 0.5f * compensation : 0f;
        _shellMat.SetVector("_CloudErosion", new Vector4(
            _clouds.erosionScale.value, _clouds.erosionFactor.value * 0.75f * compensation,
            _clouds.microErosionScale.value, micro));

        if (hdrpShapeNoise != null) _shellMat.SetTexture("_ShapeNoise", hdrpShapeNoise);
        Texture3D erosion = perlin ? hdrpPerlinErosion : hdrpWorleyErosion;
        if (erosion != null) _shellMat.SetTexture("_ErosionNoise", erosion);

        // покрытие как у патча; плотность как в HDRP: Density Multiplier² × 2 (значение у земли)
        _shellMat.SetVector("_CloudCoverage", new Vector4(
            coverageThreshold, coverageMultiplier, _groundDensity * _groundDensity * 2f * shellOpacity, offset.y));

        // Altitude Distortion (в шейдере умножается на высоту в слое), толщина слоя, степень покрытия
        float theta = WindOrientationDeg() * Mathf.Deg2Rad;
        Vector2 distortion = new Vector2(-Mathf.Cos(theta), -Mathf.Sin(theta)) * (_clouds.altitudeDistortion.value * 0.25f);
        _shellMat.SetVector("_CloudMisc", new Vector4(distortion.x, distortion.y,
                                                      _clouds.altitudeRange.value, coverageGamma));

        _shellMat.SetVector("_CloudFade", new Vector4(detailFadeStartKm * 1000f, detailFadeEndKm * 1000f, 0f, 0f));
    }

    /// <summary>
    /// Направление ветра облаков в градусах — как WindOrientationParameter.GetValue в HDRP, но без HDCamera:
    /// значение Global берётся из Visual Environment того же skyVolume (нет его там — 0).
    /// </summary>
    float WindOrientationDeg()
    {
        var wind = _clouds.orientation.value;
        if (wind.mode == WindParameter.WindOverrideMode.Custom) return wind.customValue;

        float global = skyVolume.profile.TryGet(out VisualEnvironment env) ? env.windOrientation.value : 0f;
        return wind.mode == WindParameter.WindOverrideMode.Additive ? global + wind.additiveValue : global;
    }

#if UNITY_EDITOR
    const string HdrpNoiseFolder =
        "Packages/com.unity.render-pipelines.high-definition/Runtime/RenderPipelineResources/Texture/VolumetricClouds/";

    void Reset() => FindHdrpNoise();
    void OnValidate() => FindHdrpNoise();

    /// <summary>
    /// Находит в пакете HDRP те же 3D-текстуры, которыми рисуются объёмные облака.
    /// Ссылки сохраняются в сцене, поэтому в билде они тоже будут.
    /// </summary>
    void FindHdrpNoise()
    {
        if (hdrpShapeNoise == null)
            hdrpShapeNoise = UnityEditor.AssetDatabase.LoadAssetAtPath<Texture3D>(HdrpNoiseFolder + "WorleyNoise128RGBA.png");
        if (hdrpWorleyErosion == null)
            hdrpWorleyErosion = UnityEditor.AssetDatabase.LoadAssetAtPath<Texture3D>(HdrpNoiseFolder + "WorleyNoise32RGB.png");
        if (hdrpPerlinErosion == null)
            hdrpPerlinErosion = UnityEditor.AssetDatabase.LoadAssetAtPath<Texture3D>(HdrpNoiseFolder + "PerlinNoise32RGB.png");
    }
#endif

    // ==================================================================
    // Геометрия карты

    /// <summary>
    /// Поворот сферы, при котором место (startLatitude, startLongitude) карты оказывается на вершине
    /// планеты, над началом координат. Тот же поворот используется при вырезке патча — поэтому слои совпадают.
    /// </summary>
    Quaternion MapRotation
    {
        get
        {
            float lat = startLatitude * Mathf.Deg2Rad;
            float lon = (startLongitude + 180f) * Mathf.Deg2Rad;   // долгота 0 = середина картинки
            Vector3 start = new Vector3(Mathf.Cos(lat) * Mathf.Cos(lon), Mathf.Sin(lat),
                                        Mathf.Cos(lat) * Mathf.Sin(lon));
            return Quaternion.FromToRotation(start, Vector3.up);
        }
    }

    /// <summary>Направление из центра сферы (в её собственных осях) → UV карты. Та же развёртка, что в MakeSphere.</summary>
    static Vector2 DirToUV(Vector3 d)
    {
        float u = Mathf.Atan2(d.z, d.x) / (2f * Mathf.PI);
        if (u < 0f) u += 1f;
        float v = 1f - Mathf.Acos(Mathf.Clamp(d.y, -1f, 1f)) / Mathf.PI;
        return new Vector2(u, v);
    }

    /// <summary>
    /// Сфера радиуса 1 с развёрткой «долгота-широта»: U идёт по кругу вокруг оси Y, V — от южного
    /// полюса (0) к северному (1). Так натягивается обычная карта облаков планеты 2:1.
    /// </summary>
    static Mesh MakeSphere(int lonSegments)
    {
        int latSegments = lonSegments / 2;
        var verts = new Vector3[(lonSegments + 1) * (latSegments + 1)];
        var uvs = new Vector2[verts.Length];
        var tris = new int[lonSegments * latSegments * 6];

        int v = 0;
        for (int lat = 0; lat <= latSegments; lat++)
        {
            float theta = Mathf.PI * lat / latSegments;   // 0 = северный полюс
            for (int lon = 0; lon <= lonSegments; lon++)
            {
                float phi = 2f * Mathf.PI * lon / lonSegments;
                verts[v] = new Vector3(Mathf.Sin(theta) * Mathf.Cos(phi), Mathf.Cos(theta),
                                       Mathf.Sin(theta) * Mathf.Sin(phi));
                uvs[v] = new Vector2((float)lon / lonSegments, 1f - (float)lat / latSegments);
                v++;
            }
        }

        int t = 0;
        for (int lat = 0; lat < latSegments; lat++)
            for (int lon = 0; lon < lonSegments; lon++)
            {
                int a = lat * (lonSegments + 1) + lon;
                int b = a + lonSegments + 1;
                tris[t++] = a; tris[t++] = a + 1; tris[t++] = b;
                tris[t++] = b; tris[t++] = a + 1; tris[t++] = b + 1;
            }

        var mesh = new Mesh { name = "CloudShellSphere" };
        mesh.vertices = verts;
        mesh.normals = verts;
        mesh.uv = uvs;
        mesh.triangles = tris;
        mesh.RecalculateBounds();
        return mesh;
    }

    // ==================================================================
    // Панель отладки

    void OnGUI()
    {
        if (!showDebug) return;

        const float width = 640f;
        var style = new GUIStyle(GUI.skin.label) { fontSize = 13, wordWrap = true, richText = true };
        style.normal.textColor = Color.white;

        var lines = new List<string>();
        foreach (var p in _problems) lines.Add("<color=#ffb060>! " + p + "</color>");
        if (_patchError != null) lines.Add("<color=#ffb060>! " + _patchError + "</color>");

        if (_ready)
        {
            lines.Add($"высота {_altitudeKm:F1} км   переход {_transition * 100f:F0}%   " +
                      (_transition <= 0.001f ? "(только объёмные)" : _transition >= 0.999f ? "(только сфера)" : "(оба слоя)"));
            if (_altitudeKm < -1f)
                lines.Add("<color=#ffb060>! высота отрицательная — planetRadiusKm не совпадает с Planet Radius?</color>");

            Vector2 m = WorldXZToMinimap(player.position.x, player.position.z);
            bool inside = Mathf.Abs(m.x) <= 0.5f && Mathf.Abs(m.y) <= 0.5f;
            lines.Add($"патч {patchSizeKm:F0} км, пиксель {patchSizeKm / PatchResolution:F1} км   " +
                      (inside ? "камера над патчем" : "<color=#ffb060>камера за патчем — объёмные облака повторяют его копию</color>"));
            lines.Add($"до горизонта ~{_horizonKm:F0} км" +
                      (_horizonKm > patchSizeKm / 2f && _transition < 0.999f
                          ? " — дальше края патча (серые клетки на мини-карте)" : ""));
            if (_cam != null) lines.Add($"Far Clip {_cam.farClipPlane / 1000f:F0} км");
        }

        float textHeight = 0f;
        foreach (var l in lines) textHeight += style.CalcHeight(new GUIContent(l), width);
        bool maps = _ready && _patch != null;
        GUI.Box(new Rect(5, 5, width + 10, textHeight + (maps ? 290f : 10f)), GUIContent.none);

        float y = 10f;
        foreach (var l in lines)
        {
            float h = style.CalcHeight(new GUIContent(l), width);
            GUI.Label(new Rect(10, y, width, h), l, style);
            y += h;
        }
        if (!maps) return;

        y += 6f;
        var small = new GUIStyle(style) { fontSize = 11 };

        DrawMinimap(new Rect(10, y, 240, 240));
        GUI.Label(new Rect(10, y + 242, 240, 40),
                  "жёлтое — патч, серое — его копии, белая точка — камера, красная линия — взгляд, голубое — горизонт",
                  small);

        if (_patchSource != null)
        {
            var r = new Rect(260, y, 380, 190);
            GUI.DrawTexture(r, _patchSource, ScaleMode.StretchToFill, false);
            DrawPatchOnPlanetMap(r);
            GUI.Label(new Rect(260, y + 192, 380, 40),
                      "карта облаков планеты: жёлтое — патч, белая точка — камера", small);
        }
    }

    /// <summary>
    /// Точка мира (x, z) → координаты мини-карты: центральный патч занимает от -0.5 до 0.5,
    /// +X вправо, +Z вверх.
    /// </summary>
    Vector2 WorldXZToMinimap(float x, float z) => new Vector2(x / PatchSizeM, z / PatchSizeM);

    /// <summary>
    /// Мини-карта 3×3 патча: в центре настоящий патч (с жёлтой рамкой), вокруг — копии, которые HDRP
    /// повторяет дальше. Поверх — камера, направление взгляда и круг горизонта.
    /// Патч рисуется перевёрнутым по вертикали, чтобы +Z мира смотрел вверх (см. PatchPixelToWorldXZ).
    /// </summary>
    void DrawMinimap(Rect r)
    {
        float tile = r.width / 3f;
        Color prev = GUI.color;
        var flipped = new Rect(0f, 1f, 1f, -1f);

        for (int ty = 0; ty < 3; ty++)
            for (int tx = 0; tx < 3; tx++)
            {
                GUI.color = (tx == 1 && ty == 1) ? Color.white : new Color(0.45f, 0.45f, 0.45f);
                GUI.DrawTextureWithTexCoords(new Rect(r.x + tx * tile, r.y + ty * tile, tile, tile), _patch, flipped);
            }
        GUI.color = prev;
        RectOutline(new Rect(r.x + tile, r.y + tile, tile, tile), 2f, Color.yellow);

        // камера (экранный Y смотрит вниз, поэтому минус)
        Vector2 m = WorldXZToMinimap(player.position.x, player.position.z);
        Vector2 cam = new Vector2(r.center.x + m.x * tile, r.center.y - m.y * tile);

        // горизонт
        float horizon = _horizonKm / patchSizeKm * tile;
        for (int k = 0; k < 120; k++)
        {
            float a = k / 120f * Mathf.PI * 2f;
            Vector2 q = cam + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * horizon;
            if (r.Contains(q)) Dot(q, 3, Color.cyan);
        }

        // направление взгляда
        Vector3 forward = _cam != null ? _cam.transform.forward : player.forward;
        Vector2 look = new Vector2(forward.x, forward.z);
        if (look.sqrMagnitude > 1e-6f)
        {
            look.Normalize();
            for (int k = 2; k < 28; k += 2)
            {
                Vector2 q = cam + new Vector2(look.x, -look.y) * k;
                if (r.Contains(q)) Dot(q, 3, Color.red);
            }
        }

        if (r.Contains(cam)) Dot(cam, 8, Color.white);
        else GUI.Label(new Rect(r.x + 4, r.y + 2, r.width, 20), "камера за пределами мини-карты");
    }

    /// <summary>
    /// На превью карты планеты отмечает, откуда вырезан патч (углы и центр — жёлтым)
    /// и где сейчас камера (белым).
    /// </summary>
    void DrawPatchOnPlanetMap(Rect r)
    {
        Quaternion worldToMap = Quaternion.Inverse(MapRotation);
        float shellRadius = (planetRadiusKm + shellAltitudeKm) * 1000f;

        Vector2 ToScreen(Vector3 worldPoint)
        {
            Vector2 uv = DirToUV(worldToMap * (worldPoint - PlanetCenter).normalized);
            return new Vector2(r.x + uv.x * r.width, r.y + (1f - uv.y) * r.height);
        }

        Vector3 OverPatch(float u, float v)
        {
            Vector2 xz = PatchPixelToWorldXZ(u, v);
            float y = Mathf.Sqrt(Mathf.Max(shellRadius * shellRadius - xz.x * xz.x - xz.y * xz.y, 0f));
            return PlanetCenter + new Vector3(xz.x, y, xz.y);
        }

        foreach (var corner in new[] { new Vector2(0, 0), new Vector2(1, 0), new Vector2(0, 1), new Vector2(1, 1) })
            Dot(ToScreen(OverPatch(corner.x, corner.y)), 5, Color.yellow);
        Dot(ToScreen(OverPatch(0.5f, 0.5f)), 7, Color.yellow);
        Dot(ToScreen(player.position), 7, Color.white);
    }

    static void Dot(Vector2 center, float size, Color color)
    {
        Color prev = GUI.color;
        GUI.color = color;
        GUI.DrawTexture(new Rect(center.x - size / 2f, center.y - size / 2f, size, size), Texture2D.whiteTexture);
        GUI.color = prev;
    }

    static void RectOutline(Rect r, float thickness, Color color)
    {
        Color prev = GUI.color;
        GUI.color = color;
        GUI.DrawTexture(new Rect(r.x, r.y, r.width, thickness), Texture2D.whiteTexture);
        GUI.DrawTexture(new Rect(r.x, r.yMax - thickness, r.width, thickness), Texture2D.whiteTexture);
        GUI.DrawTexture(new Rect(r.x, r.y, thickness, r.height), Texture2D.whiteTexture);
        GUI.DrawTexture(new Rect(r.xMax - thickness, r.y, thickness, r.height), Texture2D.whiteTexture);
        GUI.color = prev;
    }
}

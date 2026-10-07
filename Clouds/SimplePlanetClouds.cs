using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

/// <summary>
/// Связка двух видов облаков:
///  - у земли: Volumetric Clouds HDRP (сквозь них можно пролететь)
///  - из космоса: сфера с текстурой облаков вокруг планеты
/// Между fadeStartKm и fadeEndKm одни плавно гаснут, другие проявляются.
///
/// Совпадение рисунка: из текстуры сферы вырезается кусок вокруг вершины планеты
/// и отдаётся объёмным облакам как карта. Всё на процессоре, без шейдеров.
///
/// Отладка: панель в левом верхнем углу, режимы layerView, жёлтая рамка патча
/// в мире и мини-карта.
/// </summary>
public class SimplePlanetClouds : MonoBehaviour
{
    [Tooltip("Камера игрока.")]
    public Transform player;

    [Tooltip("Volume, на котором стоит Volumetric Clouds (обычно Sky and Fog Volume).")]
    public Volume skyVolume;

    [Tooltip("Материал HDRP/Lit, Surface Type = Transparent, в Base Map — текстура облаков.")]
    public Material shellMaterial;

    [Tooltip("Должен совпадать с Planet Radius в Visual Environment.")]
    public float planetRadiusKm = 6371f;

    [Tooltip("На какой высоте над землёй висит сфера с облаками.")]
    public float shellAltitudeKm = 3f;

    public float fadeStartKm = 30f;
    public float fadeEndKm = 70f;

    [Tooltip("Сам подстраивать Far Clip камеры по высоте, чтобы сфера всегда была видна.")]
    public bool autoFarClip = true;

    [Tooltip("Плотность объёмных облаков у земли, если в профиле она оказалась нулевой.")]
    [Range(0.05f, 1f)] public float fallbackDensity = 0.4f;

    [Header("Где на карте облаков стоит вершина планеты")]
    [Range(-80f, 80f)] public float startLatitude = 45f;
    [Range(-180f, 180f)] public float startLongitude = -30f;

    [Header("Совпадение рисунка")]
    public bool matchPattern = true;

    [Tooltip("Читаемая копия карты облаков для вырезки (Read/Write включён, 2048–4096 хватает). " +
             "Тогда у текстуры сферы Read/Write можно выключить и поставить 8k. " +
             "Если пусто — берётся Base Map из материала сферы.")]
    public Texture2D patchSourceTexture;
    public float patchSizeKm = 1000f;
    public float hdrpTileSizeKm = 124f;
    [Range(0f, 0.9f)] public float coverageThreshold = 0.2f;
    [Range(0f, 3f)] public float coverageMultiplier = 1f;
    public bool flipX = false;
    public bool flipZ = false;
    [Tooltip("Поменять местами оси X и Z (если тестовая фигура повёрнута на 90°).")]
    public bool swapXZ = false;

    [Header("Детали сферы как у объёмных облаков")]
    [Tooltip("3D-шум формы из HDRP (WorleyNoise128RGBA). В редакторе подставляется сам.")]
    public Texture3D hdrpShapeNoise;
    [Tooltip("3D-шум эрозии HDRP Worley 32 (WorleyNoise32RGB). В редакторе подставляется сам.")]
    public Texture3D hdrpWorleyErosion;
    [Tooltip("3D-шум эрозии HDRP Perlin 32 (PerlinNoise32RGB). В редакторе подставляется сам.")]
    public Texture3D hdrpPerlinErosion;

    [Tooltip("Насколько плотной выглядит сфера в облаках. 1 — по плотности объёмных облаков.")]
    [Range(0.1f, 4f)] public float shellOpacity = 1f;

    [Tooltip("С какого расстояния от камеры детали на сфере начинают гаснуть.")]
    public float detailFadeStartKm = 150f;
    [Tooltip("С какого расстояния на сфере остаётся только карта облаков.")]
    public float detailFadeEndKm = 800f;

    [Header("Калибровка")]
    [Tooltip("Положить в объёмные облака тестовую фигуру и нарисовать красный контур там, где она должна быть.")]
    public bool testPattern = false;

    [Tooltip("Размер красного контура, логарифмическая шкала: +1 = вдвое больше.")]
    [Range(-10f, 10f)] public float outlineScale = 0f;

    [Tooltip("Контур совпал с облаками — поставьте галочку, масштаб перенесётся в hdrpTileSizeKm.")]
    public bool applyOutlineScale = false;

    [Tooltip("Живая подстройка размера объёмных облаков, логарифмическая шкала: +1 = вдвое крупнее. " +
             "В режиме Overlay двигайте, пока белое не ляжет на красное.")]
    [Range(-6f, 6f)] public float volumetricScale = 0f;

    [Tooltip("Размер подобран — поставьте галочку: он перенесётся в hdrpTileSizeKm и сохранится " +
             "после выхода из Play.")]
    public bool applyVolumetricScale = false;

    public enum LayerView
    {
        Normal,          // переход по высоте
        Both,            // оба слоя сразу
        OnlyVolumetric,  // только объёмные
        OnlyShell,       // только сфера
        Overlay          // объёмные белые + сфера красная поверх: видно, где не совпадает
    }

    [Header("Отладка")]
    public bool showDebug = true;

    [Tooltip("Normal — обычная работа. Overlay — сфера красная поверх белых объёмных облаков: " +
             "розовое = совпадает, белое = только объёмные, тёмно-красное = только сфера.")]
    public LayerView layerView = LayerView.Normal;

    [Tooltip("Цвет сферы в обычном режиме. Если сфера светлее объёмных облаков — затемните.")]
    public Color shellTint = Color.white;

    [Tooltip("Принимает ли сфера туман и атмосферную дымку. Если сфера видна только кругом под " +
             "камерой — выключите: пропадёт круг, значит, виноват туман, а не Far Clip.")]
    public bool shellReceiveFog = true;

    [Tooltip("Жёлтая рамка в мире: граница, внутри которой объёмные облака повторяют текстуру.")]
    public bool showPatchBorder = true;

    [Tooltip("Мини-карта в панели: патч, его копии, вы, взгляд и горизонт.")]
    public bool showMinimap = true;

    // ------------------------------------------------------------------

    VolumetricClouds _clouds;
    float _groundDensity;
    Camera _cam;
    float _baseFarClip;
    float _farNeeded;
    float _farWeSet = -1f;
    bool _farOverridden;
    int _lastFog = -1;

    GameObject _shell;
    Material _shellMat;
    Texture2D _patch;
    Texture _sourceTex;
    Vector2 _patchCenterUV;

    GameObject _outline;
    GameObject _border;
    string _lastSettings;
    float _lastOutlineScale;
    float _lastVolumetricScale;

    float _altitudeKm;
    float _horizonKm;

    readonly List<string> _startLog = new List<string>();
    readonly List<string> _liveLog = new List<string>();

    Vector3 PlanetCenter => new Vector3(0f, -planetRadiusKm * 1000f, 0f);

    // hdrpTileSizeKm с учётом живого ползунка. Больше ползунок -> меньше tiling -> облака крупнее.
    float EffectiveTileKm => hdrpTileSizeKm * Mathf.Pow(2f, -volumetricScale);
    float TilingK => EffectiveTileKm / patchSizeKm;

    // Поворот сферы: нужная точка карты оказывается на вершине планеты.
    // Используется и для сферы, и для вырезки — поэтому слои совпадают.
    Quaternion MapRotation
    {
        get
        {
            float lat = startLatitude * Mathf.Deg2Rad;
            float phi = (startLongitude + 180f) * Mathf.Deg2Rad;   // долгота 0 = центр картинки
            Vector3 d = new Vector3(Mathf.Cos(lat) * Mathf.Cos(phi), Mathf.Sin(lat),
                                    Mathf.Cos(lat) * Mathf.Sin(phi));
            return Quaternion.FromToRotation(d, Vector3.up);
        }
    }

    void Ok(string msg) { _startLog.Add("OK    " + msg); Debug.Log("[Clouds] OK: " + msg, this); }
    void Warn(string msg) { _startLog.Add("ВНИМ  " + msg); Debug.LogWarning("[Clouds] " + msg, this); }
    void Fail(string msg) { _startLog.Add("ОШИБКА " + msg); Debug.LogError("[Clouds] " + msg, this); }

    // ==================================================================
    // Запуск

    void Start()
    {
        _startLog.Clear();

        // --- 1. Ссылки ---
        if (player == null) Fail("1. не задан player");
        else Ok($"1. player = {player.name}");

        if (skyVolume == null) { Fail("1. не задан skyVolume — дальше нельзя"); return; }
        if (shellMaterial == null) { Fail("1. не задан shellMaterial — дальше нельзя"); return; }
        Ok($"1. skyVolume = {skyVolume.name}, shellMaterial = {shellMaterial.name} ({shellMaterial.shader.name})");

        // --- 2. Volumetric Clouds ---
        CheckOtherVolumes();

        skyVolume.profile.TryGet(out _clouds);   // .profile — копия на время игры, ассет не портится
        if (_clouds == null)
        {
            Fail("2. в профиле skyVolume нет override Volumetric Clouds");
        }
        else
        {
            Ok($"2. Volumetric Clouds найден. override активен: {_clouds.active}, " +
               $"State: {(_clouds.enable.overrideState ? _clouds.enable.value.ToString() : "не задан (галочка слева выключена)")}");

            _groundDensity = _clouds.densityMultiplier.value;
            if (_groundDensity < 0.01f)
            {
                Warn($"2. Density Multiplier в профиле = {_groundDensity:F2}. Использую {fallbackDensity:F2}. " +
                     "Исправьте значение в профиле.");
                _groundDensity = fallbackDensity;
            }
            else Ok($"2. плотность у земли = {_groundDensity:F2}");
        }

        // --- 3. Сфера ---
        try
        {
            _shellMat = new Material(shellMaterial);
            _shell = new GameObject("Cloud Shell");
            _shell.AddComponent<MeshFilter>().mesh = MakeSphere(256);
            var mr = _shell.AddComponent<MeshRenderer>();
            mr.material = _shellMat;
            mr.shadowCastingMode = ShadowCastingMode.Off;
            _shell.transform.SetPositionAndRotation(PlanetCenter, MapRotation);

            Ok($"3. сфера создана, радиус {planetRadiusKm + shellAltitudeKm:F0} км");
            Ok($"3. материал сферы принимает туман: {_shellMat.IsKeywordEnabled("_ENABLE_FOG_ON_TRANSPARENT")}");

            if (!_shellMat.HasProperty("_BaseColor"))
                Fail("3. у материала нет _BaseColor — это не HDRP/Lit?");
            if (_shellMat.GetFloat("_SurfaceType") < 0.5f)
                Warn("3. материал сферы Opaque — поставьте Surface Type = Transparent");
            else
                Ok("3. материал сферы Transparent");

            if (!_shellMat.HasProperty("_CloudShape"))
                Warn("3. у материала сферы нет _CloudShape — это старый граф, детали как у объёмных не появятся");
            else if (hdrpShapeNoise == null || hdrpWorleyErosion == null || hdrpPerlinErosion == null)
                Warn("3. не заданы 3D-шумы HDRP (hdrpShapeNoise, hdrpWorleyErosion, hdrpPerlinErosion)");
            else
                Ok("3. детали сферы: шум формы и эрозии HDRP");
        }
        catch (System.Exception e)
        {
            Fail("3. не удалось создать сферу: " + e.Message);
        }

        // --- 4. Камера ---
        CheckCamera();

        // --- 5. Совпадение рисунка ---
        RebuildPatchStuff();
        _lastSettings = SettingsKey();
        _lastOutlineScale = outlineScale;
    }

    void OnEnable()
    {
        RenderPipelineManager.beginCameraRendering += OnBeginCameraRendering;
    }

    /// <summary>Вызывается прямо перед рендером каждой камеры — после всех Update и
    /// LateUpdate, в том числе после Cinemachine. Здесь наш Far Clip уже никто не перебьёт.</summary>
    void OnBeginCameraRendering(ScriptableRenderContext context, Camera cam)
    {
        if (!autoFarClip || cam != _cam || _farNeeded <= 0f) return;

        if (_farWeSet > 0f && !Mathf.Approximately(cam.farClipPlane, _farWeSet))
            _farOverridden = true;   // между нашим Update и рендером кто-то поменял значение

        cam.farClipPlane = _farNeeded;
        _farWeSet = _farNeeded;
    }

    void OnDisable()
    {
        RenderPipelineManager.beginCameraRendering -= OnBeginCameraRendering;
        if (_cam != null && _baseFarClip > 0f) _cam.farClipPlane = _baseFarClip;
        if (_outline != null) Destroy(_outline);
        if (_border != null) Destroy(_border);
    }

    void CheckOtherVolumes()
    {
        var volumes = FindObjectsByType<Volume>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
        foreach (var v in volumes)
        {
            if (v == skyVolume || v.sharedProfile == null) continue;
            if (v.sharedProfile.Has<VolumetricClouds>() && v.priority >= skyVolume.priority)
                Warn($"2. Volume '{v.name}' (priority {v.priority}) тоже содержит Volumetric Clouds " +
                     $"и может перекрывать skyVolume (priority {skyVolume.priority})");
        }
    }

    void CheckCamera()
    {
        Camera cam = player != null ? player.GetComponent<Camera>() : null;
        if (cam == null) cam = Camera.main;
        if (cam == null) { Warn("4. камера не найдена"); return; }

        _cam = cam;
        _baseFarClip = cam.farClipPlane;

        if (player != null && player.GetComponent<Camera>() == null)
            Warn($"4. на объекте player ('{player.name}') нет камеры — беру Camera.main ('{cam.name}'). " +
                 "Лучше указать в player сам объект камеры");

        // другие включённые камеры, которые могут оказаться в Game view вместо нашей
        foreach (var c in FindObjectsByType<Camera>(FindObjectsSortMode.None))
        {
            if (c == cam || !c.enabled || !c.gameObject.activeInHierarchy || c.targetTexture != null) continue;
            if (c.depth >= cam.depth)
                Warn($"4. есть ещё камера '{c.name}' (depth {c.depth} >= {cam.depth}) — " +
                     $"Game view может показывать её, а не '{cam.name}'");
        }

        if (cam.GetComponent("CinemachineBrain") != null)
            Warn("4. на камере CinemachineBrain — он сам задаёт Far Clip из Lens виртуальной камеры. " +
                 "Скрипт перебивает его перед рендером, но надёжнее поставить Far Clip в Lens побольше");
        if (autoFarClip)
            Ok($"4. Far Clip камеры '{cam.name}' настраивается автоматически");
        else
            Ok($"4. Far Clip = {cam.farClipPlane / 1000f:F0} км (ручной)");
    }

    // ==================================================================
    // Каждый кадр

    void Update()
    {
        _liveLog.Clear();
        if (player == null || _shell == null || _shellMat == null)
        {
            _liveLog.Add("не работает — см. шаги запуска");
            return;
        }

        _altitudeKm = (Vector3.Distance(player.position, PlanetCenter) / 1000f) - planetRadiusKm;
        double rr = planetRadiusKm, hh = rr + System.Math.Max(_altitudeKm, 0f);
        _horizonKm = (float)System.Math.Sqrt(hh * hh - rr * rr);

        float t = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(fadeStartKm, fadeEndKm, _altitudeKm));

        UpdateFarClip();
        HandleSettingsChanges();

        int fog = shellReceiveFog ? 1 : 0;
        if (fog != _lastFog)
        {
            _lastFog = fog;
            _shellMat.SetFloat("_EnableFogOnTransparent", fog);
            if (shellReceiveFog) _shellMat.EnableKeyword("_ENABLE_FOG_ON_TRANSPARENT");
            else _shellMat.DisableKeyword("_ENABLE_FOG_ON_TRANSPARENT");
        }

        // --- какие слои показывать ---
        float shellAlpha, volumetricFade, lift = 0f;
        Color tint = shellTint;
        switch (layerView)
        {
            case LayerView.Both: shellAlpha = 1f; volumetricFade = 0f; break;
            case LayerView.OnlyVolumetric: shellAlpha = 0f; volumetricFade = 0f; break;
            case LayerView.OnlyShell: shellAlpha = 1f; volumetricFade = 1f; break;
            case LayerView.Overlay:
                shellAlpha = 0.55f; volumetricFade = 0f;
                tint = new Color(1f, 0.1f, 0.1f);
                lift = 2f;   // приподнять сферу над верхушками объёмных облаков
                break;
            default: shellAlpha = t; volumetricFade = t; break;
        }

        if (_clouds != null)
        {
            _clouds.densityMultiplier.Override(_groundDensity * (1f - volumetricFade));
            _clouds.enable.Override(volumetricFade < 0.999f);
        }

        tint.a = shellAlpha;
        _shellMat.SetColor("_BaseColor", tint);
        _shell.transform.localScale = Vector3.one * (planetRadiusKm + shellAltitudeKm + lift) * 1000f;
        _shell.SetActive(shellAlpha > 0.001f);
        UpdateShellDetail();

        if (_border != null) _border.SetActive(showPatchBorder);

        FillLiveLog(t, shellAlpha, volumetricFade);
    }

    void HandleSettingsChanges()
    {
        if (applyOutlineScale)
        {
            applyOutlineScale = false;
            hdrpTileSizeKm *= Mathf.Pow(2f, outlineScale);
            outlineScale = 0f;
            SaveCalibration();
        }

        if (applyVolumetricScale)
        {
            applyVolumetricScale = false;
            hdrpTileSizeKm = EffectiveTileKm;
            volumetricScale = 0f;
            _lastVolumetricScale = 0f;
            SaveCalibration();
        }

        // ползунок размера: меняем только tiling, патч пересобирать не нужно
        if (!Mathf.Approximately(volumetricScale, _lastVolumetricScale))
        {
            _lastVolumetricScale = volumetricScale;
            if (_clouds != null && matchPattern)
                _clouds.cloudTiling.Override(new Vector2(TilingK, TilingK));
        }

        if (testPattern && !Mathf.Approximately(outlineScale, _lastOutlineScale))
        {
            _lastOutlineScale = outlineScale;
            BuildOutline();
        }

        if (SettingsKey() != _lastSettings)
        {
            _lastSettings = SettingsKey();
            _startLog.RemoveAll(l => l.Contains(" 5. "));
            _shell.transform.rotation = MapRotation;
            RebuildPatchStuff();
        }
    }

    /// <summary>Передаёт в материал сферы настройки формы и эрозии из Volumetric Clouds,
    /// чтобы шейдер считал края облаков по той же формуле, что и HDRP (CloudShellHDRP.hlsl).</summary>
    void UpdateShellDetail()
    {
        if (_clouds == null || !_shellMat.HasProperty("_CloudShape")) return;

        float bottom = planetRadiusKm * 1000f + _clouds.bottomAltitude.value;
        Vector3 c = PlanetCenter;
        _shellMat.SetVector("_CloudLayer", new Vector4(c.x, c.y, c.z, bottom));

        Vector3 offset = _clouds.shapeOffset.value;
        _shellMat.SetVector("_CloudShape", new Vector4(_clouds.shapeScale.value, _clouds.shapeFactor.value,
                                                       offset.x, offset.z));

        // как ErosionNoiseTypeToTexture / ErosionNoiseTypeToErosionCompensation в HDRP
        bool perlin = _clouds.erosionNoiseType.value == VolumetricClouds.CloudErosionNoise.Perlin32;
        float compensation = perlin ? 0.75f : 1f;
        bool micro = _clouds.cloudControl.value == VolumetricClouds.CloudControl.Simple
            ? _clouds.cloudSimpleMode.value == VolumetricClouds.CloudSimpleMode.Quality
            : _clouds.microErosion.value;
        _shellMat.SetVector("_CloudErosion", new Vector4(
            _clouds.erosionScale.value, _clouds.erosionFactor.value * 0.75f * compensation,
            _clouds.microErosionScale.value, micro ? _clouds.microErosionFactor.value * 0.5f * compensation : 0f));

        if (hdrpShapeNoise != null) _shellMat.SetTexture("_ShapeNoise", hdrpShapeNoise);
        Texture3D erosion = perlin ? hdrpPerlinErosion : hdrpWorleyErosion;
        if (erosion != null) _shellMat.SetTexture("_ErosionNoise", erosion);

        // покрытие как у патча, умноженное на Cumulus Map Multiplier;
        // плотность как в HDRP: Density Multiplier^2 * 2 (берём значение у земли, до перехода)
        _shellMat.SetVector("_CloudCoverage", new Vector4(
            coverageThreshold, coverageMultiplier * _clouds.cumulusMapMultiplier.value,
            _groundDensity * _groundDensity * 2f * shellOpacity, offset.y));

        // Altitude Distortion: в шейдере умножается на высоту шага в слое
        float theta = WindOrientationDeg() * Mathf.Deg2Rad;
        Vector2 distortion = new Vector2(-Mathf.Cos(theta), -Mathf.Sin(theta)) * (_clouds.altitudeDistortion.value * 0.25f);
        _shellMat.SetVector("_CloudMisc", new Vector4(distortion.x, distortion.y, _clouds.altitudeRange.value, 0f));
        _shellMat.SetVector("_CloudFade", new Vector4(detailFadeStartKm * 1000f, detailFadeEndKm * 1000f, 0f, 0f));
    }

    /// <summary>Направление ветра облаков, как WindOrientationParameter.GetValue в HDRP, но без HDCamera:
    /// значение Global берётся из Visual Environment того же skyVolume.</summary>
    float WindOrientationDeg()
    {
        var wind = _clouds.orientation.value;
        if (wind.mode == WindParameter.WindOverrideMode.Custom) return wind.customValue;

        float global = skyVolume.profile.TryGet(out VisualEnvironment env) ? env.windOrientation.value : 0f;
        return wind.mode == WindParameter.WindOverrideMode.Additive ? global + wind.additiveValue : global;
    }

#if UNITY_EDITOR
    const string HdrpNoiseDir =
        "Packages/com.unity.render-pipelines.high-definition/Runtime/RenderPipelineResources/Texture/VolumetricClouds/";

    void Reset() => FindHdrpNoise();
    void OnValidate() => FindHdrpNoise();

    /// <summary>Находит в пакете HDRP те же 3D-текстуры, которыми рисуются объёмные облака.
    /// Ссылки сохраняются в сцене, поэтому в билде они тоже будут.</summary>
    void FindHdrpNoise()
    {
        if (hdrpShapeNoise == null)
            hdrpShapeNoise = UnityEditor.AssetDatabase.LoadAssetAtPath<Texture3D>(HdrpNoiseDir + "WorleyNoise128RGBA.png");
        if (hdrpWorleyErosion == null)
            hdrpWorleyErosion = UnityEditor.AssetDatabase.LoadAssetAtPath<Texture3D>(HdrpNoiseDir + "WorleyNoise32RGB.png");
        if (hdrpPerlinErosion == null)
            hdrpPerlinErosion = UnityEditor.AssetDatabase.LoadAssetAtPath<Texture3D>(HdrpNoiseDir + "PerlinNoise32RGB.png");
    }
#endif

    string SettingsKey() =>
        $"{matchPattern}|{patchSizeKm}|{hdrpTileSizeKm}|{coverageThreshold}|{coverageMultiplier}|" +
        $"{flipX}|{flipZ}|{swapXZ}|{testPattern}|{startLatitude}|{startLongitude}|{shellAltitudeKm}";

    void RebuildPatchStuff()
    {
        if (!matchPattern) { Ok("5. matchPattern выключен — объёмные облака рисуют своё"); }
        else if (_clouds == null) { Warn("5. пропущено: нет Volumetric Clouds"); }
        else
        {
            try { BuildPatch(); }
            catch (System.Exception e) { Fail("5. вырезка упала: " + e.GetType().Name + ": " + e.Message); }
        }
        BuildOutline();
        BuildBorder();
    }

    void FillLiveLog(float t, float shellAlpha, float volumetricFade)
    {
        if (layerView == LayerView.Overlay)
        {
            _liveLog.Add("Размер не совпадает — двигайте volumetricScale, глядя прямо вниз над точкой старта " +
                         "(белая точка в центре мини-карты), затем applyVolumetricScale.");
            _liveLog.Add("OVERLAY: розовое = совпадает,  белое = облако только у объёмных,  " +
                         "тёмно-красное = облако только на сфере. Смотрите прямо вниз.");
            _liveLog.Add("Мелкие расхождения по краям — нормально (HDRP разъедает облака шумом). " +
                         "Важно, чтобы совпадали крупные массы.");
        }

        if (testPattern)
        {
            float f = Mathf.Pow(2f, outlineScale);
            _liveLog.Add($"ТЕСТ: контур ×{f:F3}. После применения hdrpTileSizeKm станет {hdrpTileSizeKm * f:F2}");
        }

        _liveLog.Add($"высота {_altitudeKm:F1} км   переход {t * 100f:F0}%" +
                     (layerView != LayerView.Normal ? $"   [{layerView}]" : ""));
        if (_altitudeKm < -1f)
            _liveLog.Add("!! высота отрицательная: planetRadiusKm не совпадает с Planet Radius в Visual Environment?");

        if (matchPattern)
        {
            string line = $"до горизонта ~{_horizonKm:F0} км, рисунок совпадает в пределах ±{patchSizeKm / 2f:F0} км";
            if (_horizonKm > patchSizeKm / 2f && volumetricFade < 0.999f)
                line += " — дальше объёмные облака повторяют патч (серые клетки на мини-карте)";
            _liveLog.Add(line);
        }

        if (matchPattern)
            _liveLog.Add($"масштаб объёмных: hdrpTileSizeKm {hdrpTileSizeKm:F2}" +
                         (Mathf.Approximately(volumetricScale, 0f) ? "" :
                          $", с ползунком ×{Mathf.Pow(2f, volumetricScale):F2} -> {EffectiveTileKm:F2}") +
                         $"   tiling {TilingK:F4}");

        if (_clouds != null)
            _liveLog.Add($"объёмные: State {_clouds.enable.value}, плотность {_clouds.densityMultiplier.value:F2}, " +
                         $"режим {_clouds.cloudControl.value}");
        if (_cam == null)
            _liveLog.Add("!! камера не найдена — Far Clip не подстраивается");
        else
        {
            _liveLog.Add($"камера '{_cam.name}': Far Clip {_cam.farClipPlane / 1000f:F0} км" +
                         (autoFarClip ? $" (нужно {_farNeeded / 1000f:F0})" : " (ручной)"));
            if (_farOverridden)
                _liveLog.Add("!! другой скрипт меняет Far Clip каждый кадр (Cinemachine?) — перебиваем перед рендером");
        }
        _liveLog.Add($"туман на сфере: {(shellReceiveFog ? "вкл" : "выкл")}");
        _liveLog.Add($"сфера: {(_shell.activeSelf ? "видима" : "скрыта")}, альфа {shellAlpha:F2}" +
                     (_altitudeKm < fadeStartKm && layerView == LayerView.Normal
                         ? $"  (появится выше {fadeStartKm:F0} км)" : ""));
    }

    void UpdateFarClip()
    {
        if (!autoFarClip || _cam == null) return;

        double camDist = Vector3.Distance(_cam.transform.position, PlanetCenter);
        double shellRadius = (planetRadiusKm + shellAltitudeKm) * 1000.0;
        double horizon = camDist > shellRadius
            ? System.Math.Sqrt(camDist * camDist - shellRadius * shellRadius)
            : 0.0;

        _farNeeded = Mathf.Max(_baseFarClip, (float)(horizon * 1.1 + 20000.0));
        _cam.farClipPlane = _farNeeded;
        _farWeSet = _farNeeded;
    }

    void SaveCalibration()
    {
        Debug.Log($"[Clouds] калибровка применена: hdrpTileSizeKm = {hdrpTileSizeKm:F3}", this);
#if UNITY_EDITOR
        if (Application.isPlaying)
        {
            UnityEditor.EditorPrefs.SetFloat(CalibrationKeeper.Key, hdrpTileSizeKm);
            Debug.Log("[Clouds] значение будет записано в компонент после выхода из Play. " +
                      "Потом сохраните сцену.", this);
        }
#endif
    }

    // ==================================================================
    // Вырезка патча

    void BuildPatch()
    {
        _sourceTex = patchSourceTexture != null ? patchSourceTexture : shellMaterial.GetTexture("_BaseColorMap");
        var src = _sourceTex as Texture2D;
        if (src == null) { Fail("5. нет текстуры для вырезки: задайте patchSourceTexture или Base Map материала"); return; }
        if (!src.isReadable)
        {
            Fail($"5. у '{src.name}' выключен Read/Write. Включите его или задайте отдельную " +
                 "читаемую копию в patchSourceTexture");
            return;
        }

        const int res = 256;   // максимум, который принимает HDRP
        var pixels = new Color32[res * res];

        float cloudRadius = (planetRadiusKm + shellAltitudeKm) * 1000f;
        Quaternion toMap = Quaternion.Inverse(MapRotation);

        float sumOut = 0f, maxOut = 0f;
        int cloudy = 0;

        for (int j = 0; j < res; j++)
            for (int i = 0; i < res; i++)
            {
                float fx = (i + 0.5f) / res - 0.5f;
                float fz = (j + 0.5f) / res - 0.5f;
                Vector2 w = PatchToWorldXZ(fx, fz);

                float y = Mathf.Sqrt(Mathf.Max(cloudRadius * cloudRadius - w.x * w.x - w.y * w.y, 0f));
                Vector2 uv = DirToUV(toMap * (new Vector3(w.x, y, w.y) / cloudRadius));

                float a = testPattern
                    ? TestPattern(fx, fz)
                    : Mathf.Clamp01((src.GetPixelBilinear(uv.x, uv.y).a - coverageThreshold)
                                    / (1f - coverageThreshold) * coverageMultiplier);

                sumOut += a;
                if (a > maxOut) maxOut = a;
                if (a > 0.1f) cloudy++;

                byte b = (byte)(a * 255f);
                pixels[j * res + i] = new Color32(b, b, b, 255);
            }

        int n = res * res;
        _patchCenterUV = DirToUV(toMap * Vector3.up);

        Ok($"5. источник вырезки: {src.name} {src.width}x{src.height}" +
           (patchSourceTexture != null ? " (отдельная копия)" : " (Base Map материала)"));

        if (maxOut <= 0f)
            Fail($"5. патч пустой. Уменьшите coverageThreshold или поменяйте startLatitude/startLongitude");
        else
            Ok($"5. патч {patchSizeKm:F0} км: покрытие {sumOut / n:F2}, облачных пикселей {cloudy * 100f / n:F0}%, " +
               $"{patchSizeKm / res:F1} км на пиксель{(testPattern ? "  [ТЕСТ]" : "")}");

        if (_patch != null) Destroy(_patch);
        _patch = new Texture2D(res, res, TextureFormat.RGBA32, false, true)
        {
            name = "CloudPatch",
            wrapMode = TextureWrapMode.Repeat
        };
        _patch.SetPixels32(pixels);
        _patch.Apply(false, true);

        _clouds.cloudControl.Override(VolumetricClouds.CloudControl.Advanced);
        _clouds.cloudMapResolution.Override(VolumetricClouds.CloudMapResolution.Ultra256x256);
        _clouds.cumulusMap.Override(_patch);
        _clouds.cloudTiling.Override(new Vector2(TilingK, TilingK));
    }

    /// <summary>Пиксель патча (-0.5..0.5) -> точка мира по X и Z, метры.
    /// Одна функция для вырезки, контура, рамки и мини-карты.</summary>
    Vector2 PatchToWorldXZ(float fx, float fz)
    {
        float size = patchSizeKm * 1000f;
        float x = fx * size, z = fz * size;
        if (swapXZ) { float tmp = x; x = z; z = tmp; }
        if (flipX) x = -x;
        if (flipZ) z = -z;
        return new Vector2(x, z);
    }

    /// <summary>Обратное преобразование: мир -> координаты патча.
    /// Для направлений (isDirection) без деления на размер.</summary>
    Vector2 WorldXZToPatch(float x, float z, bool isDirection = false)
    {
        if (flipX) x = -x;
        if (flipZ) z = -z;
        if (swapXZ) { float tmp = x; x = z; z = tmp; }
        if (isDirection) return new Vector2(x, z);
        float size = patchSizeKm * 1000f;
        return new Vector2(x / size, z / size);
    }

    static float TestPattern(float fx, float fz)
    {
        if (Mathf.Abs(fx) < 0.25f && Mathf.Abs(fz) < 0.25f) return 1f;          // квадрат
        if (fx > 0.25f && fx < 0.45f && Mathf.Abs(fz) < 0.02f) return 1f;       // длинная полоса к +X
        if (fz > 0.25f && fz < 0.35f && Mathf.Abs(fx) < 0.02f) return 1f;       // короткая к +Z
        return 0f;
    }

    static Vector2 DirToUV(Vector3 d)   // та же развёртка, что у сферы в MakeSphere
    {
        float u = Mathf.Atan2(d.z, d.x) / (2f * Mathf.PI);
        if (u < 0f) u += 1f;
        float v = 1f - Mathf.Acos(Mathf.Clamp(d.y, -1f, 1f)) / Mathf.PI;
        return new Vector2(u, v);
    }

    // ==================================================================
    // Линии в мире: контур теста и рамка патча

    void BuildOutline()
    {
        if (_outline != null) Destroy(_outline);
        if (!testPattern) return;

        var mat = MakeLineMaterial(Color.red);
        if (mat == null) return;

        float s = Mathf.Pow(2f, outlineScale);
        float width = patchSizeKm * 1000f * 0.008f * s;
        _outline = new GameObject("Cloud Test Outline");

        AddLine(_outline.transform, mat, width, true, s, new[] {
            new Vector2(-0.25f, -0.25f), new Vector2(0.25f, -0.25f),
            new Vector2(0.25f, 0.25f), new Vector2(-0.25f, 0.25f) });
        AddLine(_outline.transform, mat, width, false, s, new[] { new Vector2(0.25f, 0f), new Vector2(0.45f, 0f) });
        AddLine(_outline.transform, mat, width, false, s, new[] { new Vector2(0f, 0.25f), new Vector2(0f, 0.35f) });
    }

    void BuildBorder()
    {
        if (_border != null) Destroy(_border);
        if (!matchPattern) return;

        var mat = MakeLineMaterial(Color.yellow);
        if (mat == null) return;

        _border = new GameObject("Cloud Patch Border");
        AddLine(_border.transform, mat, patchSizeKm * 1000f * 0.004f, true, 1f, new[] {
            new Vector2(-0.5f, -0.5f), new Vector2(0.5f, -0.5f),
            new Vector2(0.5f, 0.5f), new Vector2(-0.5f, 0.5f) });
        _border.SetActive(showPatchBorder);
    }

    Material MakeLineMaterial(Color c)
    {
        var shader = Shader.Find("HDRP/Unlit");
        if (shader == null) { Warn("5. не найден шейдер HDRP/Unlit — линии не нарисовать"); return null; }
        var m = new Material(shader);
        m.SetColor("_UnlitColor", c);
        return m;
    }

    void AddLine(Transform parent, Material mat, float width, bool loop, float scale, Vector2[] points)
    {
        var go = new GameObject("line");
        go.transform.SetParent(parent, false);
        var lr = go.AddComponent<LineRenderer>();
        lr.sharedMaterial = mat;
        lr.useWorldSpace = true;
        lr.loop = loop;
        lr.widthMultiplier = width;
        lr.shadowCastingMode = ShadowCastingMode.Off;

        // дробим отрезки, чтобы линия шла по кривизне планеты
        var pts = new List<Vector3>();
        int segs = loop ? points.Length : points.Length - 1;
        for (int sgm = 0; sgm < segs; sgm++)
        {
            Vector2 a = points[sgm], b = points[(sgm + 1) % points.Length];
            for (int k = 0; k < 32; k++) pts.Add(PatchToWorldPoint(Vector2.Lerp(a, b, k / 32f) * scale));
        }
        if (!loop) pts.Add(PatchToWorldPoint(points[points.Length - 1] * scale));

        lr.positionCount = pts.Count;
        lr.SetPositions(pts.ToArray());
    }

    Vector3 PatchToWorldPoint(Vector2 f)
    {
        Vector2 w = PatchToWorldXZ(f.x, f.y);
        float r = planetRadiusKm * 1000f;
        float alt = (shellAltitudeKm + 0.5f) * 1000f;
        Vector3 dir = new Vector3(w.x, Mathf.Sqrt(Mathf.Max(r * r - w.x * w.x - w.y * w.y, 0f)), w.y).normalized;
        return PlanetCenter + dir * (r + alt);
    }

    // ==================================================================
    // Панель отладки

    void OnGUI()
    {
        if (!showDebug) return;

        var style = new GUIStyle(GUI.skin.label) { fontSize = 13, wordWrap = true, richText = true };
        style.normal.textColor = Color.white;

        const float w = 640f;
        float y = 10f;

        // считаем высоту заранее, чтобы фон был под всем
        float textH = 44f;
        foreach (var l in _startLog) textH += style.CalcHeight(new GUIContent(l), w);
        foreach (var l in _liveLog) textH += style.CalcHeight(new GUIContent(l), w);
        GUI.Box(new Rect(5, 5, w + 10, textH + 290f), GUIContent.none);

        GUI.Label(new Rect(10, y, w, 20), "<b>Запуск</b>", style);
        y += 20;
        foreach (var line in _startLog)
        {
            var s = new GUIStyle(style);
            if (line.StartsWith("ОШИБКА")) s.normal.textColor = new Color(1f, 0.45f, 0.45f);
            else if (line.StartsWith("ВНИМ")) s.normal.textColor = new Color(1f, 0.85f, 0.4f);
            float h = s.CalcHeight(new GUIContent(line), w);
            GUI.Label(new Rect(10, y, w, h), line, s);
            y += h;
        }

        y += 4;
        GUI.Label(new Rect(10, y, w, 20), "<b>Сейчас</b>", style);
        y += 20;
        foreach (var line in _liveLog)
        {
            float h = style.CalcHeight(new GUIContent(line), w);
            GUI.Label(new Rect(10, y, w, h), line, style);
            y += h;
        }

        y += 8;
        if (showMinimap && _patch != null)
        {
            DrawMinimap(new Rect(10, y, 240, 240));
            GUI.Label(new Rect(10, y + 242, 240, 40),
                      "жёлтое — патч, серое — его копии, белая точка — вы, красная линия — взгляд, голубое — горизонт",
                      new GUIStyle(style) { fontSize = 11 });
        }
        if (_sourceTex != null)
        {
            var r = new Rect(260, y, 380, 190);
            GUI.DrawTexture(r, _sourceTex, ScaleMode.StretchToFill, false);
            Dot(new Vector2(r.x + _patchCenterUV.x * r.width, r.y + (1f - _patchCenterUV.y) * r.height), 8, Color.red);
            GUI.Label(new Rect(260, y + 192, 380, 20), "текстура сферы (красное — центр патча)", style);
        }
    }

    void DrawMinimap(Rect r)
    {
        float tile = r.width / 3f;
        Color prev = GUI.color;

        // 3x3: в центре настоящий патч, вокруг копии, которые HDRP повторяет
        for (int ty = 0; ty < 3; ty++)
            for (int tx = 0; tx < 3; tx++)
            {
                GUI.color = (tx == 1 && ty == 1) ? Color.white : new Color(0.4f, 0.4f, 0.4f);
                GUI.DrawTexture(new Rect(r.x + tx * tile, r.y + ty * tile, tile, tile), _patch, ScaleMode.StretchToFill, false);
            }
        GUI.color = prev;
        RectOutline(new Rect(r.x + tile, r.y + tile, tile, tile), 2f, Color.yellow);

        if (player == null) return;

        // где вы (в координатах патча; y на экране вниз, поэтому минус)
        Vector2 p = WorldXZToPatch(player.position.x, player.position.z);
        Vector2 sp = new Vector2(r.center.x + p.x * tile, r.center.y - p.y * tile);

        // горизонт
        float hr = _horizonKm / patchSizeKm * tile;
        for (int k = 0; k < 120; k++)
        {
            float a = k / 120f * Mathf.PI * 2f;
            Vector2 q = sp + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * hr;
            if (r.Contains(q)) Dot(q, 3, Color.cyan);
        }

        // направление взгляда
        Vector3 fwd = _cam != null ? _cam.transform.forward : player.forward;
        Vector2 fd = WorldXZToPatch(fwd.x, fwd.z, true);
        if (fd.sqrMagnitude > 1e-6f)
        {
            fd.Normalize();
            for (int k = 2; k < 28; k += 2)
            {
                Vector2 q = sp + new Vector2(fd.x, -fd.y) * k;
                if (r.Contains(q)) Dot(q, 3, Color.red);
            }
        }

        if (r.Contains(sp)) Dot(sp, 8, Color.white);
        else GUI.Label(new Rect(r.x, r.y, r.width, 20), "вы за пределами мини-карты");
    }

    static void Dot(Vector2 c, float size, Color col)
    {
        Color prev = GUI.color;
        GUI.color = col;
        GUI.DrawTexture(new Rect(c.x - size / 2f, c.y - size / 2f, size, size), Texture2D.whiteTexture);
        GUI.color = prev;
    }

    static void RectOutline(Rect r, float t, Color col)
    {
        Color prev = GUI.color;
        GUI.color = col;
        GUI.DrawTexture(new Rect(r.x, r.y, r.width, t), Texture2D.whiteTexture);
        GUI.DrawTexture(new Rect(r.x, r.yMax - t, r.width, t), Texture2D.whiteTexture);
        GUI.DrawTexture(new Rect(r.x, r.y, t, r.height), Texture2D.whiteTexture);
        GUI.DrawTexture(new Rect(r.xMax - t, r.y, t, r.height), Texture2D.whiteTexture);
        GUI.color = prev;
    }

    // ==================================================================

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
}

#if UNITY_EDITOR
/// <summary>После выхода из Play записывает в компонент калибровку, сделанную во время игры.
/// Иначе Unity откатила бы её вместе со всеми остальными изменениями из Play.</summary>
[UnityEditor.InitializeOnLoad]
static class CalibrationKeeper
{
    public const string Key = "SimplePlanetClouds.PendingTileSizeKm";

    static CalibrationKeeper()
    {
        UnityEditor.EditorApplication.playModeStateChanged += state =>
        {
            if (state != UnityEditor.PlayModeStateChange.EnteredEditMode) return;
            if (!UnityEditor.EditorPrefs.HasKey(Key)) return;

            float value = UnityEditor.EditorPrefs.GetFloat(Key);
            UnityEditor.EditorPrefs.DeleteKey(Key);

            foreach (var c in Object.FindObjectsByType<SimplePlanetClouds>(FindObjectsSortMode.None))
            {
                UnityEditor.Undo.RecordObject(c, "Apply cloud calibration");
                c.hdrpTileSizeKm = value;
                UnityEditor.EditorUtility.SetDirty(c);
                UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(c.gameObject.scene);
            }
            Debug.Log($"[Clouds] калибровка hdrpTileSizeKm = {value:F3} записана в компонент. Сохраните сцену (Ctrl+S).");
        };
    }
}
#endif
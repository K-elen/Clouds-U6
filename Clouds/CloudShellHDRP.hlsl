#ifndef CLOUD_SHELL_HDRP_INCLUDED
#define CLOUD_SHELL_HDRP_INCLUDED

// Прозрачность облачной сферы по той же формуле, что у Volumetric Clouds HDRP
// (EvaluateCloudProperties в VolumetricCloudsUtilities.hlsl, Unity 6 / HDRP 17):
// те же 3D-текстуры шума, те же координаты, тот же DensityRemap для формы и эрозии.
//
// Сверху облако видно насквозь по вертикали, поэтому здесь короткий марш из 4 шагов
// через слой, где HDRP рисует кучевые облака в режиме Advanced (только Cumulus Map):
// это нижние ~2–24% высоты слоя. Плотность и эрозия по высоте взяты из встроенной
// таблицы HDRP CloudLutRainAO (столбец кучевых, мип 1).
//
// Все параметры кладёт в материал SimplePlanetClouds.cs из Volume, так что
// поменяли Shape/Erosion в Volumetric Clouds — сфера поменялась вместе с ними.

#define CLOUD_SHELL_NOISE_NORMALIZATION 100000.0   // NOISE_TEXTURE_NORMALIZATION_FACTOR в HDRP
#define CLOUD_SHELL_STEPS 4

// высота шага в долях слоя и значения CloudLutRainAO на ней: плотность, эрозия
static const float  kCloudShellHeight[CLOUD_SHELL_STEPS]  = { 0.045, 0.095, 0.145, 0.195 };
static const float  kCloudShellDensity[CLOUD_SHELL_STEPS] = { 0.68,  0.988, 0.986, 0.52  };
static const float  kCloudShellErosion[CLOUD_SHELL_STEPS] = { 0.85,  0.915, 0.978, 0.998 };
#define CLOUD_SHELL_STEP_FRACTION 0.05   // толщина шага в долях слоя

// DensityRemap(x, a, 1, 0, 1) из HDRP
float CloudShellRemap(float x, float a)
{
    return (x - a) / max(1.0 - a, 1e-4);
}

// Layer    = центр планеты (xyz, абсолютные мировые координаты), радиус низа слоя (w), метры
// Shape    = Shape Scale, Shape Factor, Shape Offset X, Shape Offset Z
// Erosion  = Erosion Scale, сила эрозии, Micro Erosion Scale, сила микроэрозии (0 = выкл)
// Coverage = порог покрытия, множитель покрытия, плотность, Shape Offset Y
// Misc     = сдвиг от Altitude Distortion (xy), толщина слоя в метрах (z), степень покрытия coverageGamma (w)
// Fade     = с какого расстояния детали гаснут, где пропадают совсем (метры)
void CloudShellAlpha_float(UnityTexture2D CloudMap, float2 UV, float3 PositionWS, float3 PositionAWS,
                           UnityTexture3D ShapeNoise, UnityTexture3D ErosionNoise,
                           float4 Layer, float4 Shape, float4 Erosion, float4 Coverage, float4 Misc,
                           float2 Fade, float4 BaseColor, out float Alpha)
{
    float A = SAMPLE_TEXTURE2D(CloudMap.tex, CloudMap.samplerstate, UV).a;

    // покрытие — так же, как скрипт режет патч, и как HDRP читает Cumulus Map
    float cov = saturate((A - Coverage.x) / max(1.0 - Coverage.x, 1e-4) * Coverage.y);
    cov = pow(cov, Misc.w > 0.0 ? Misc.w : 1.0);
    cov = cov < 0.01 ? 0.0 : cov;

    float3 up = normalize(PositionAWS - Layer.xyz);
    float thickness = Misc.z;
    float opticalDepth = 0.0;

    [unroll]
    for (int i = 0; i < CLOUD_SHELL_STEPS; i++)
    {
        float h = kCloudShellHeight[i];
        float3 ps = up * (Layer.w + h * thickness);   // точка в координатах планеты

        // --- форма: Worley 128, координаты как в AnimateShapeNoisePosition (без ветра) ---
        float3 sp = ps;
        sp.y += sp.x / 3.0 + sp.z / 7.0;
        float3 shapeUVW = sp.xzy / CLOUD_SHELL_NOISE_NORMALIZATION * Shape.x
                        - float3(Shape.z, Shape.w, Coverage.w)
                        + h * float3(Misc.xy, 0.0);
        float low = SAMPLE_TEXTURE3D(ShapeNoise.tex, ShapeNoise.samplerstate, shapeUVW).r;

        float shapeFactor = lerp(0.1, 1.0, Shape.y) * kCloudShellErosion[i];
        low = lerp(1.0, low, shapeFactor);
        float baseCloud = 1.0 - kCloudShellDensity[i] * cov * (1.0 - shapeFactor);
        float d = saturate(CloudShellRemap(low, baseCloud)) * cov * cov;

        // --- эрозия: та же текстура (Perlin 32 или Worley 32) и тот же масштаб ---
        float e = 1.0 - SAMPLE_TEXTURE3D(ErosionNoise.tex, ErosionNoise.samplerstate,
                                         ps / CLOUD_SHELL_NOISE_NORMALIZATION * Erosion.x).r;
        d = CloudShellRemap(d, e * Erosion.y * kCloudShellErosion[i] * cov);

        // --- микроэрозия (Erosion.w = 0, если выключена: тогда remap ничего не меняет) ---
        float m = 1.0 - SAMPLE_TEXTURE3D(ErosionNoise.tex, ErosionNoise.samplerstate,
                                         ps / CLOUD_SHELL_NOISE_NORMALIZATION * Erosion.z).r;
        d = CloudShellRemap(d, m * Erosion.w * kCloudShellErosion[i] * cov);

        opticalDepth += max(d, 0.0);
    }

    // Непрозрачность столба сверху: 1 - exp(-сумма плотностей * sigma * длина шага).
    // sigma = 0.04 (без Rain Map), плотность HDRP = Density Multiplier^2 * 2 — в Coverage.z.
    // Под углом луч идёт сквозь слой длиннее, чем по вертикали, — как и у лучей HDRP.
    float slant = 1.0 / max(abs(dot(normalize(PositionWS), up)), 0.1);
    float detailed = 1.0 - exp(-opticalDepth * 0.04 * Coverage.z * CLOUD_SHELL_STEP_FRACTION * thickness * slant);

    // Вдали детали мельче пикселя — плавно переходим к карте как есть.
    // PositionWS в HDRP отсчитывается от камеры.
    float fade = 1.0 - smoothstep(Fade.x, Fade.y, length(PositionWS));

    Alpha = saturate(lerp(A, detailed, fade)) * BaseColor.a;
}

#endif

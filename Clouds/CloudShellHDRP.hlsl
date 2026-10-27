#ifndef CLOUD_SHELL_HDRP_INCLUDED
#define CLOUD_SHELL_HDRP_INCLUDED

// Прозрачность облачной сферы по той же формуле, что у Volumetric Clouds HDRP
// (EvaluateCloudProperties в VolumetricCloudsUtilities.hlsl, Unity 6 / HDRP 17):
// те же 3D-текстуры шума, те же координаты, тот же DensityRemap для формы и эрозии.
//
// Типы облаков — как в HDRP в режиме Advanced. Из карты облаков и карты типов (R — кучевые,
// G — слоистые, B — кучево-дождевые) считается то же, что HDRP получает из Cumulus, Alto Stratus
// и Cumulonimbus Map (CloudMapGenerator.compute): покрытие, тип и наибольшая высота облаков.
// Профиль облака по высоте берётся из той же встроенной таблицы HDRP (CloudLutRainAO): у кучевых
// облака внизу слоя, у слоистых — в его середине, у грозовых — почти на всю высоту.
//
// Сверху облако видно насквозь по вертикали, поэтому здесь короткий марш из 8 шагов по высоте —
// только в той полосе слоя, где облака этого типа есть по таблице (у слоистых она тонкая, 29–47%,
// и шаги по всей высоте её бы просто перешагнули).
//
// Все параметры кладёт в материал SimplePlanetClouds.cs из Volume, так что
// поменяли Shape/Erosion в Volumetric Clouds — сфера поменялась вместе с ними.

#define CLOUD_SHELL_NOISE_NORMALIZATION 100000.0   // NOISE_TEXTURE_NORMALIZATION_FACTOR в HDRP
#define CLOUD_SHELL_STEPS 8
#define CLOUD_SHELL_SHAPE_NOISE_SIZE 128.0         // WorleyNoise128RGBA
#define CLOUD_SHELL_EROSION_NOISE_SIZE 32.0        // PerlinNoise32RGB / WorleyNoise32RGB

#define CLOUD_SHELL_TYPE_FULL_AT 0.25    // TypeFullAt в SimplePlanetClouds.cs
#define CLOUD_SHELL_STORM_CORE_AT 0.5    // StormCoreAt в SimplePlanetClouds.cs

// Значения трёх карт HDRP из альфы карты облаков и долей типов — ровно как MapInputs в
// SimplePlanetClouds.cs (там же подробно, почему так):
//  - покрытие: порог и множитель, затем 0..1 растягивается на start..1 (до ~0.4 HDRP не рисует ничего);
//  - тип в полную силу, как только его доля больше 25% (иначе на границе типов пустой шов);
//  - грозовые ядра — только где их доля не меньше половины.
void CloudShellMapInputs(float A, float3 types, float4 Coverage, float start,
                         out float cumulus, out float stratus, out float cumulonimbus)
{
    float cov = saturate((A - Coverage.x) / max(1.0 - Coverage.x, 1e-4) * Coverage.y);
    cov = cov > 0.0 ? lerp(start, 1.0, cov) : 0.0;   // ясное небо остаётся нулём
    cumulus = cov * saturate(types.r / CLOUD_SHELL_TYPE_FULL_AT);
    stratus = cov * saturate(types.g / CLOUD_SHELL_TYPE_FULL_AT);
    cumulonimbus = types.b >= CLOUD_SHELL_STORM_CORE_AT ? cov * types.b : 0.0;
}

// DensityRemap(x, a, 1, 0, 1) из HDRP
float CloudShellRemap(float x, float a)
{
    return (x - a) / max(1.0 - a, 1e-4);
}

// Что HDRP получил бы из трёх карт в этой точке (CloudMapGenerator.compute, множители карт = 1).
// cumulus, stratus, cumulonimbus — значения в картах; на выходе покрытие, тип (столбец таблицы)
// и полоса высот в долях слоя, где у этого типа есть облака (по таблице CloudLutRainAO).
void CloudShellMapData(float cumulus, float stratus, float cumulonimbus,
                       out float coverage, out float cloudType, out float bandBottom, out float bandTop)
{
    float maxHeight;
    cumulus = cumulus < 0.01 ? 0.0 : cumulus;
    stratus = stratus < 0.01 ? 0.0 : stratus;
    cumulonimbus = cumulonimbus < 0.01 ? 0.0 : cumulonimbus;

    coverage = 0.0; cloudType = 0.0; bandBottom = 0.0; bandTop = 0.0;
    if (cumulonimbus > 0.0)                       // грозовые главнее всех; значение — развитость облака
    {
        cloudType = cumulonimbus * (1.0 - 128.0 / 256.0) + 128.0 / 256.0;
        float grow = saturate((cloudType - 130.0 / 256.0) / (6.0 / 256.0));
        coverage = cloudType < 136.0 / 256.0 ? grow : 0.75;
        maxHeight = cloudType < 136.0 / 256.0 ? grow : 1.0;
        bandBottom = 0.02; bandTop = clamp(maxHeight, bandBottom, 0.95);   // едва начавшаяся гроза — полосы нет
    }
    else if (cumulus > 0.0)
    {
        if (stratus > 0.0)                        // кучевые со слоистыми — смешанный тип
        {
            cloudType = 48.0 / 256.0;
            coverage = max(cumulus, stratus);
            bandBottom = 0.02; bandTop = 0.47;
        }
        else
        {
            cloudType = 96.0 / 256.0;
            coverage = cumulus;
            bandBottom = 0.02; bandTop = 0.24;   // HDRP ограничивает кучевые половиной слоя, а по таблице они до 24%
        }
    }
    else if (stratus > 0.0)
    {
        cloudType = 16.0 / 256.0;
        coverage = stratus;
        bandBottom = 0.29; bandTop = 0.47;
    }
}

// Уровень мипа для шума: сколько текселей шума приходится на пиксель экрана.
// Мип считаем сами, чтобы шум можно было читать в цикле и внутри условий.
float CloudShellNoiseLod(float3 uvw, float textureSize)
{
    float3 dx = ddx(uvw), dy = ddy(uvw);
    return max(0.0, log2(max(length(dx), length(dy)) * textureSize));
}

// Layer    = центр планеты (xyz, абсолютные мировые координаты), радиус низа слоя (w), метры
// Shape    = Shape Scale, Shape Factor, Shape Offset X, Shape Offset Z
// Erosion  = Erosion Scale, сила эрозии, Micro Erosion Scale, сила микроэрозии (0 = выкл)
// Coverage = порог покрытия, множитель покрытия, плотность, Shape Offset Y
// Misc     = сдвиг шума эрозии по X и Z, метры (xy; как ветер у HDRP), толщина слоя в метрах (z),
//            с какого покрытия начинаются облака coverageStart (w)
// Fade     = с какого расстояния детали гаснут (x) и где пропадают совсем (y), метры; zw не используются
void CloudShellAlpha_float(UnityTexture2D CloudMap, float2 UV, float3 PositionWS, float3 PositionAWS,
                           UnityTexture3D ShapeNoise, UnityTexture3D ErosionNoise,
                           UnityTexture2D CloudTypes, UnityTexture2D CloudLut,
                           float4 Layer, float4 Shape, float4 Erosion, float4 Coverage, float4 Misc,
                           float4 Fade, float4 BaseColor, out float Alpha)
{
    float A = SAMPLE_TEXTURE2D(CloudMap.tex, CloudMap.samplerstate, UV).a;

    // значения трёх карт — так же, как скрипт режет патчи, — и то, что HDRP получил бы из них
    float3 types = SAMPLE_TEXTURE2D(CloudTypes.tex, CloudTypes.samplerstate, UV).rgb;
    float cumulus, stratus, cumulonimbus;
    CloudShellMapInputs(A, types, Coverage, Misc.w, cumulus, stratus, cumulonimbus);
    float coverage, cloudType, bandBottom, bandTop;
    CloudShellMapData(cumulus, stratus, cumulonimbus, coverage, cloudType, bandBottom, bandTop);

    float3 up = normalize(PositionAWS - Layer.xyz);
    float thickness = Misc.z;
    float3 erosionShift = float3(Misc.x, 0.0, Misc.y);

    // мипы шума — по координатам на нижней границе слоя (по высоте они меняются мало)
    float3 ps0 = up * Layer.w;
    float3 sp0 = ps0; sp0.y += sp0.x / 3.0 + sp0.z / 7.0;
    float lodShape = CloudShellNoiseLod(sp0.xzy / CLOUD_SHELL_NOISE_NORMALIZATION * Shape.x,
                                        CLOUD_SHELL_SHAPE_NOISE_SIZE);
    float lodErosion = CloudShellNoiseLod((ps0 + erosionShift) / CLOUD_SHELL_NOISE_NORMALIZATION * Erosion.x,
                                          CLOUD_SHELL_EROSION_NOISE_SIZE);
    float lodMicro = CloudShellNoiseLod((ps0 + erosionShift) / CLOUD_SHELL_NOISE_NORMALIZATION * Erosion.z,
                                        CLOUD_SHELL_EROSION_NOISE_SIZE);

    float opticalDepth = 0.0;
    float stepFraction = (bandTop - bandBottom) / CLOUD_SHELL_STEPS;   // толщина шага в долях слоя

    [branch]
    if (coverage > 0.001)   // ясное небо — шагать незачем
    {
        for (int i = 0; i < CLOUD_SHELL_STEPS; i++)
        {
            float h = bandBottom + (i + 0.5) * stepFraction;   // высота шага в долях слоя
            float3 ps = up * (Layer.w + h * thickness);   // точка в координатах планеты

            // профиль типа на этой высоте: x — плотность, y — эрозия (HDRP читает таблицу с мипа 1)
            float2 lut = SAMPLE_TEXTURE2D_LOD(CloudLut.tex, CloudLut.samplerstate, float2(cloudType, h), 1.0).xy;

            // --- форма: Worley 128, координаты как в AnimateShapeNoisePosition ---
            float3 sp = ps;
            sp.y += sp.x / 3.0 + sp.z / 7.0;
            float3 shapeUVW = sp.xzy / CLOUD_SHELL_NOISE_NORMALIZATION * Shape.x
                            - float3(Shape.z, Shape.w, Coverage.w);
            float low = SAMPLE_TEXTURE3D_LOD(ShapeNoise.tex, ShapeNoise.samplerstate, shapeUVW, lodShape).r;

            float shapeFactor = lerp(0.1, 1.0, Shape.y) * lut.y;
            low = lerp(1.0, low, shapeFactor);
            float baseCloud = 1.0 - lut.x * coverage * (1.0 - shapeFactor);
            float d = saturate(CloudShellRemap(low, baseCloud)) * coverage * coverage;

            // --- эрозия: та же текстура (Perlin 32 или Worley 32), тот же масштаб и тот же сдвиг ---
            float3 eps = ps + erosionShift;
            float e = 1.0 - SAMPLE_TEXTURE3D_LOD(ErosionNoise.tex, ErosionNoise.samplerstate,
                                                 eps / CLOUD_SHELL_NOISE_NORMALIZATION * Erosion.x, lodErosion).r;
            d = CloudShellRemap(d, e * Erosion.y * lut.y * coverage);

            // --- микроэрозия (Erosion.w = 0, если выключена: тогда remap ничего не меняет) ---
            float m = 1.0 - SAMPLE_TEXTURE3D_LOD(ErosionNoise.tex, ErosionNoise.samplerstate,
                                                 eps / CLOUD_SHELL_NOISE_NORMALIZATION * Erosion.z, lodMicro).r;
            d = CloudShellRemap(d, m * Erosion.w * lut.y * coverage);

            opticalDepth += max(d, 0.0);
        }
    }

    // Непрозрачность столба сверху: 1 - exp(-сумма плотностей * sigma * длина шага).
    // sigma = 0.04 (без Rain Map), плотность HDRP = Density Multiplier^2 * 2 — в Coverage.z.
    // Под углом луч идёт сквозь слой длиннее, чем по вертикали, — как и у лучей HDRP.
    float slant = 1.0 / max(abs(dot(normalize(PositionWS), up)), 0.1);
    float detailed = 1.0 - exp(-opticalDepth * 0.04 * Coverage.z * stepFraction * thickness * slant);

    // Вдали детали мельче пикселя — плавно переходим к карте как есть.
    // PositionWS в HDRP отсчитывается от камеры.
    float dist = length(PositionWS);
    float fade = 1.0 - smoothstep(Fade.x, Fade.y, dist);

    // Сфера видна только по мере перехода по высоте (альфа BaseColor).
    Alpha = saturate(lerp(A, detailed, fade)) * BaseColor.a;
}

#endif

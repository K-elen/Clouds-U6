using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

/// <summary>
/// Планета, которая движется вместо камеры.
///
/// Камера всегда стоит в начале координат, а центр планеты — прямо под ней. Движение «камеры»
/// превращается в движение планеты (метод Fly):
///  - вверх и вниз — центр планеты отодвигается и приближается;
///  - по горизонтали — планета поворачивается вокруг своего центра, подставляя под камеру новое место.
///    Так можно облететь весь шар; солнце при этом само оказывается в другом месте неба.
///
/// Fly вызывает контроллер (PlanetFlyController). Сама камера должна стоять в начале координат.
///
/// Ссылок задавать не нужно: Volume с облаками и Visual Environment скрипт находит в сцене сам.
///
/// Для HDRP центр планеты задаётся в Visual Environment (Center Mode = Manual). Повороты планеты HDRP
/// не знает — объёмные облака под поворот подстраивает SimplePlanetClouds по PlanarTravel.
/// </summary>
[DefaultExecutionOrder(-100)]   // раньше остальных скриптов: они в этом кадре уже видят новое положение планеты
public class FloatingPlanet : MonoBehaviour
{
    // ==================================================================
    // Настройки

    [Header("Планета")]
    [Tooltip("Радиус планеты, км. Скрипт сам ставит его в Planet Radius в Visual Environment " +
             "(в HDRP по умолчанию 6378.1).")]
    public float radiusKm = 6378.1f;

    [Tooltip("Корневой объект со всем, что стоит на планете: рельеф, постройки, объекты. " +
             "Он едет и поворачивается вместе с планетой. Пусто — на планете нет ничего, кроме неба и облаков.")]
    public Transform planetContent;

    // ==================================================================
    // Состояние

    double _altitudeShiftM;              // на сколько планета отодвинулась вниз от начального положения
    double _planarX, _planarZ;           // пройдено по горизонтали — как по плоской карте, в осях мира
    Quaternion _rotation = Quaternion.identity;   // поворот планеты от начального положения

    VisualEnvironment _environment;      // из копии профиля SkyVolume
    Vector3 _contentOffset;              // где planetContent стоял относительно центра планеты в начале
    Quaternion _contentRotation;         // и как был повёрнут

    /// <summary>
    /// Volume с Volumetric Clouds (обычно Sky and Fog Volume) — найден в сцене сам. В нём же скрипт
    /// держит Visual Environment. Профиль меняется только в копии на время игры.
    /// </summary>
    public Volume SkyVolume { get; private set; }

    /// <summary>Центр планеты в мире, метры. Всегда прямо под камерой; в начале — (0, -радиус, 0).</summary>
    public Vector3 Center => new Vector3(0f, (float)(-radiusKm * 1000.0 - _altitudeShiftM), 0f);

    /// <summary>Поворот планеты от начального положения. Его нужно применять ко всему, что на ней нарисовано.</summary>
    public Quaternion Rotation => _rotation;

    /// <summary>
    /// Сколько пройдено по горизонтали, метры, в осях мира — как если бы камера летела над плоской картой
    /// облачного слоя. По нему SimplePlanetClouds сдвигает карту и шум объёмных облаков HDRP.
    /// </summary>
    public double PlanarX => _planarX;
    public double PlanarZ => _planarZ;

    /// <summary>Сколько пролетели от старта, км (для отладки): X и Z — по горизонтали, Y — вверх.</summary>
    public Vector3 TravelKm =>
        new Vector3((float)(_planarX / 1000.0), (float)(_altitudeShiftM / 1000.0), (float)(_planarZ / 1000.0));

    // ==================================================================

    // Awake, а не Start: SkyVolume нужен другим скриптам уже в их Start.
    void Awake()
    {
        SkyVolume = FindSkyVolume();

        if (planetContent != null)
        {
            _contentOffset = planetContent.position - Center;
            _contentRotation = planetContent.rotation;
        }

        // Камера стоит в сцене не в начале координат — переносим её туда, а её место «долетаем» планетой.
        var cam = Camera.main;
        if (cam != null && cam.transform.position != Vector3.zero)
        {
            Fly(cam.transform.position);
            cam.transform.position = Vector3.zero;
        }

        SetupVisualEnvironment();
    }

    // В LateUpdate: контроллер уже сделал свой ход за этот кадр, а рендер ещё не начался.
    void LateUpdate()
    {
        PlaceContent();
        UpdateVisualEnvironment();
    }

    /// <summary>
    /// Двигает «камеру» на displacement (метры, в осях мира), на деле двигая планету.
    /// Вертикальная часть отодвигает центр планеты. Горизонтальная поворачивает планету: место, лежащее
    /// впереди, подъезжает под камеру. Угол поворота — пройденное расстояние, делённое на расстояние
    /// от центра до камеры, то есть на высоте планета поворачивается медленнее, как при настоящем полёте.
    /// </summary>
    public void Fly(Vector3 displacement)
    {
        _altitudeShiftM += displacement.y;

        Vector3 horizontal = new Vector3(displacement.x, 0f, displacement.z);
        float distance = horizontal.magnitude;
        if (distance <= 0f) return;

        double cameraRadius = radiusKm * 1000.0 + _altitudeShiftM;   // от центра планеты до камеры
        double angle = distance / cameraRadius;                     // радианы

        // Поворот вокруг оси «направление полёта × вверх» переводит точку впереди к вершине планеты.
        Vector3 axis = Vector3.Cross(horizontal / distance, Vector3.up);
        _rotation = Quaternion.Normalize(Quaternion.AngleAxis((float)(angle * Mathf.Rad2Deg), axis) * _rotation);

        // На плоской карте облачного слоя тот же поворот — это сдвиг на угол × радиус планеты.
        double planar = angle * radiusKm * 1000.0;
        _planarX += horizontal.x / distance * planar;
        _planarZ += horizontal.z / distance * planar;
    }

    /// <summary>
    /// Ставит всё содержимое планеты туда, где сейчас планета: то же положение относительно её центра,
    /// повёрнутое вместе с ней. Считается от начального положения, поэтому ошибки не накапливаются.
    /// </summary>
    void PlaceContent()
    {
        if (planetContent == null) return;
        planetContent.SetPositionAndRotation(Center + _rotation * _contentOffset, _rotation * _contentRotation);
        Physics.SyncTransforms();   // чтобы коллайдеры и CharacterController сразу узнали новые позиции
    }

    /// <summary>
    /// Берёт Visual Environment из SkyVolume (нет — добавляет) и переводит его на ручной центр планеты:
    /// Rendering Space = World, Center Mode = Manual. Радиус и центр ставятся каждый кадр.
    /// Если Visual Environment есть ещё в одном Volume с большим приоритетом — он может перебить эти значения.
    /// </summary>
    void SetupVisualEnvironment()
    {
        if (SkyVolume == null)
        {
            Debug.LogWarning("[FloatingPlanet] в сцене нет Volume с Volumetric Clouds — " +
                             "HDRP не узнает, где центр планеты", this);
            return;
        }

        if (!SkyVolume.profile.TryGet(out _environment))   // .profile — копия на время игры
            _environment = SkyVolume.profile.Add<VisualEnvironment>();

        _environment.renderingSpace.Override(RenderingSpace.World);
        _environment.centerMode.Override(VisualEnvironment.PlanetMode.Manual);
        UpdateVisualEnvironment();
    }

    /// <summary>
    /// Ищет в сцене Volume, в профиле которого есть Volumetric Clouds. Если таких несколько —
    /// берёт с наибольшим приоритетом (он и побеждает при смешивании).
    /// </summary>
    static Volume FindSkyVolume()
    {
        Volume best = null;
        foreach (var v in FindObjectsByType<Volume>(FindObjectsSortMode.None))
            if (v.sharedProfile != null && v.sharedProfile.Has<VolumetricClouds>() &&
                (best == null || v.priority > best.priority))
                best = v;
        return best;
    }

    /// <summary>Центр и радиус планеты для HDRP. Visual Environment хранит их в километрах.</summary>
    void UpdateVisualEnvironment()
    {
        if (_environment == null) return;
        _environment.planetRadius.Override(radiusKm);
        _environment.planetCenter.Override(Center / 1000f);
    }
}

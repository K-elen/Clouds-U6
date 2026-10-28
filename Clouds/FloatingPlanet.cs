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
/// Радиус планеты — Planet Radius из Visual Environment того же Volume (в HDRP по умолчанию 6378.1 км):
/// задаётся там, скрипт только читает его. Центр планеты скрипт сам ставит в Visual Environment
/// (Center Mode = Manual). Повороты планеты HDRP
/// не знает — объёмные облака под поворот подстраивает SimplePlanetClouds по PlanarTravel.
/// </summary>
[DefaultExecutionOrder(-100)]   // раньше остальных скриптов: они в этом кадре уже видят новое положение планеты
public class FloatingPlanet : MonoBehaviour
{
    // ==================================================================
    // Настройки

    [Header("Планета")]
    [Tooltip("Корневой объект со всем, что стоит на планете: рельеф, постройки, объекты. " +
             "Он едет и поворачивается вместе с планетой. Пусто — на планете нет ничего, кроме неба и облаков.")]
    public Transform planetContent;

    // ==================================================================
    // Состояние

    double _altitudeShiftM;              // на сколько планета отодвинулась вниз от начального положения
    double _planarX, _planarZ;           // пройдено по горизонтали — как по плоской карте, в осях мира
    // Поворот планеты от начального положения — кватернион в double. За кадр планета поворачивается
    // на миллионные доли градуса; во float такие шаги со временем начинают теряться, а предметы рядом
    // с камерой (в 6000 км от центра) дрожали бы на полметра.
    double _qx, _qy, _qz, _qw = 1.0;

    VisualEnvironment _environment;      // из копии профиля SkyVolume
    const float DefaultRadiusKm = 6378.1f;   // радиус по умолчанию в HDRP — если Volume не найден
    Vector3 _contentOffset;              // где planetContent стоял относительно центра планеты в начале
    Quaternion _contentRotation;         // и как был повёрнут

    /// <summary>
    /// Volume с Volumetric Clouds (обычно Sky and Fog Volume) — найден в сцене сам. В нём же скрипт
    /// держит Visual Environment. Профиль меняется только в копии на время игры.
    /// </summary>
    public Volume SkyVolume { get; private set; }

    /// <summary>
    /// Радиус планеты, км, — Planet Radius из Visual Environment в SkyVolume. Меняется там же,
    /// в том числе прямо в игре.
    /// </summary>
    public float RadiusKm => _environment != null ? _environment.planetRadius.value : DefaultRadiusKm;

    /// <summary>Центр планеты в мире, метры. Всегда прямо под камерой; в начале — (0, -радиус, 0).</summary>
    public Vector3 Center => new Vector3(0f, (float)(-RadiusKm * 1000.0 - _altitudeShiftM), 0f);

    /// <summary>Поворот планеты от начального положения. Его нужно применять ко всему, что на ней нарисовано.</summary>
    public Quaternion Rotation => new Quaternion((float)_qx, (float)_qy, (float)_qz, (float)_qw);

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
        SetupVisualEnvironment();   // сначала — из него берётся радиус планеты

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

        double cameraRadius = RadiusKm * 1000.0 + _altitudeShiftM;   // от центра планеты до камеры
        double angle = distance / cameraRadius;                     // радианы

        // Поворот вокруг оси «направление полёта × вверх» переводит точку впереди к вершине планеты.
        Vector3 axis = Vector3.Cross(horizontal / distance, Vector3.up);
        RotatePlanet(axis, angle);

        // На плоской карте облачного слоя тот же поворот — это сдвиг на угол × радиус планеты.
        double planar = angle * RadiusKm * 1000.0;
        _planarX += horizontal.x / distance * planar;
        _planarZ += horizontal.z / distance * planar;
    }

    /// <summary>
    /// Поворачивает планету на angle радиан вокруг оси axis (единичной, в осях мира): новый поворот =
    /// этот поворот × прежний. Обычное умножение кватернионов, только в double.
    /// </summary>
    void RotatePlanet(Vector3 axis, double angle)
    {
        double s = System.Math.Sin(angle * 0.5), c = System.Math.Cos(angle * 0.5);
        double ax = axis.x * s, ay = axis.y * s, az = axis.z * s;

        double x = c * _qx + ax * _qw + ay * _qz - az * _qy;
        double y = c * _qy + ay * _qw + az * _qx - ax * _qz;
        double z = c * _qz + az * _qw + ax * _qy - ay * _qx;
        double w = c * _qw - ax * _qx - ay * _qy - az * _qz;

        double n = System.Math.Sqrt(x * x + y * y + z * z + w * w);
        _qx = x / n; _qy = y / n; _qz = z / n; _qw = w / n;
    }

    /// <summary>Поворачивает вектор v поворотом планеты, в double: v + 2·u×(u×v + w·v), где u = (qx, qy, qz).</summary>
    void RotateVector(Vector3 v, out double x, out double y, out double z)
    {
        double tx = _qy * v.z - _qz * v.y + _qw * v.x;
        double ty = _qz * v.x - _qx * v.z + _qw * v.y;
        double tz = _qx * v.y - _qy * v.x + _qw * v.z;
        x = v.x + 2.0 * (_qy * tz - _qz * ty);
        y = v.y + 2.0 * (_qz * tx - _qx * tz);
        z = v.z + 2.0 * (_qx * ty - _qy * tx);
    }

    /// <summary>
    /// Ставит всё содержимое планеты туда, где сейчас планета: то же положение относительно её центра,
    /// повёрнутое вместе с ней. Считается от начального положения, поэтому ошибки не накапливаются.
    /// </summary>
    void PlaceContent()
    {
        if (planetContent == null) return;
        // Положение считаем в double: и центр, и повёрнутый сдвиг — тысячи километров, а их сумма у камеры — метры.
        RotateVector(_contentOffset, out double x, out double y, out double z);
        double centerY = -RadiusKm * 1000.0 - _altitudeShiftM;
        planetContent.SetPositionAndRotation(new Vector3((float)x, (float)(centerY + y), (float)z),
                                             Rotation * _contentRotation);
        Physics.SyncTransforms();   // чтобы коллайдеры и CharacterController сразу узнали новые позиции
    }

    /// <summary>
    /// Берёт Visual Environment из SkyVolume (нет — добавляет) и переводит его на ручной центр планеты:
    /// Rendering Space = World, Center Mode = Manual. Центр ставится каждый кадр, радиус берётся
    /// из Planet Radius этого Visual Environment (если его не было — HDRP по умолчанию, 6378.1 км).
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

    /// <summary>Центр планеты для HDRP. Visual Environment хранит его в километрах.</summary>
    void UpdateVisualEnvironment()
    {
        if (_environment == null) return;
        _environment.planetCenter.Override(Center / 1000f);
    }
}

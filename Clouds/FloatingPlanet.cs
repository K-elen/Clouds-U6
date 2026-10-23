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
/// Fly вызывает контроллер (PlanetFlyController). Если вместо него камеру двигает обычный контроллер,
/// этот скрипт сам в конце кадра вернёт камеру в начало координат и превратит её сдвиг в Fly.
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

    [Tooltip("Volume с Visual Environment (обычно Sky and Fog Volume). Если Visual Environment в нём нет — " +
             "скрипт добавит его в копию профиля на время игры, сам ассет не меняется.")]
    public Volume skyVolume;

    [Tooltip("Корневой объект со всем, что стоит на планете: рельеф, постройки, объекты. " +
             "Он едет и поворачивается вместе с планетой. Пусто — на планете нет ничего, кроме неба и облаков.")]
    public Transform planetContent;

    [Header("Камера")]
    [Tooltip("Камера, которая всегда стоит в начале координат. Пусто — Camera.main.")]
    public Transform cameraTransform;

    [Tooltip("Только если камеру двигает обычный контроллер: что именно он двигает (корень игрока " +
             "или риг камеры). Скрипт вернёт это назад, а сдвиг передаст планете. Пусто — сама камера.")]
    public Transform movingRoot;

    [Tooltip("Только для обычного контроллера: на сколько метров камере можно отойти от начала координат, " +
             "прежде чем сдвиг перейдёт на планету. 0 — каждый кадр.")]
    [Min(0f)] public float recenterDistanceM = 0f;

    // ==================================================================
    // Состояние

    double _altitudeShiftM;              // на сколько планета отодвинулась вниз от начального положения
    double _planarX, _planarZ;           // пройдено по горизонтали — как по плоской карте, в осях мира
    Quaternion _rotation = Quaternion.identity;   // поворот планеты от начального положения

    VisualEnvironment _environment;      // из копии профиля skyVolume
    Vector3 _contentOffset;              // где planetContent стоял относительно центра планеты в начале
    Quaternion _contentRotation;         // и как был повёрнут

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

    void Start()
    {
        if (cameraTransform == null && Camera.main != null) cameraTransform = Camera.main.transform;

        if (planetContent != null)
        {
            _contentOffset = planetContent.position - Center;
            _contentRotation = planetContent.rotation;
        }

        SetupVisualEnvironment();
    }

    // В LateUpdate: контроллер уже сделал свой ход за этот кадр, а рендер ещё не начался.
    void LateUpdate()
    {
        TakeOverCameraMovement();
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
    /// Для обычного контроллера, который двигает саму камеру: возвращает камеру в начало координат
    /// и передаёт её сдвиг в Fly. Двигается movingRoot (или сама камера) и её Rigidbody, если он есть.
    /// </summary>
    void TakeOverCameraMovement()
    {
        if (cameraTransform == null) return;

        Vector3 shift = cameraTransform.position;   // где камера сейчас; должна быть в (0, 0, 0)
        if (shift == Vector3.zero || shift.sqrMagnitude < recenterDistanceM * recenterDistanceM) return;

        Transform root = movingRoot != null ? movingRoot : cameraTransform;
        root.position -= shift;
        var body = root.GetComponent<Rigidbody>();
        if (body != null) body.position -= shift;

        Fly(shift);
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
    /// Берёт Visual Environment из skyVolume (нет — добавляет) и переводит его на ручной центр планеты:
    /// Rendering Space = World, Center Mode = Manual. Радиус и центр ставятся каждый кадр.
    /// Если Visual Environment есть и в Volume с большим приоритетом — он может перебить эти значения.
    /// </summary>
    void SetupVisualEnvironment()
    {
        if (skyVolume == null)
        {
            Debug.LogWarning("[FloatingPlanet] не задан skyVolume — HDRP не узнает, где центр планеты", this);
            return;
        }

        if (!skyVolume.profile.TryGet(out _environment))   // .profile — копия на время игры
            _environment = skyVolume.profile.Add<VisualEnvironment>();

        _environment.renderingSpace.Override(RenderingSpace.World);
        _environment.centerMode.Override(VisualEnvironment.PlanetMode.Manual);
        UpdateVisualEnvironment();
    }

    /// <summary>Центр и радиус планеты для HDRP. Visual Environment хранит их в километрах.</summary>
    void UpdateVisualEnvironment()
    {
        if (_environment == null) return;
        _environment.planetRadius.Override(radiusKm);
        _environment.planetCenter.Override(Center / 1000f);
    }
}

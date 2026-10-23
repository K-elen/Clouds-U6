using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

/// <summary>
/// Планета, которая движется вместо камеры.
///
/// Камера всегда остаётся в начале координат. Ваш контроллер двигает её как обычно, а в конце кадра
/// этот скрипт возвращает её в (0, 0, 0) и переносит сдвиг на планету: на её центр в Visual Environment
/// (Center Mode = Manual), на всё, что стоит на планете (planetContent), и на всех, кто берёт
/// центр планеты отсюда (например, SimplePlanetClouds).
///
/// Для HDRP это то же самое, что полёт камеры: небо, атмосфера и облака считаются от центра
/// планеты. Можно стоять на земле, подняться сквозь облака и улететь в космос, а вдали от старта
/// ничего не дрожит из-за точности float — камера всегда рядом с началом координат.
/// </summary>
[DefaultExecutionOrder(-100)]   // раньше остальных скриптов: они в этом кадре уже видят новый центр планеты
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

    [Header("Камера стоит, движется планета")]
    [Tooltip("Камера, которая должна оставаться в начале координат. Пусто — Camera.main.")]
    public Transform cameraTransform;

    [Tooltip("Что двигает ваш контроллер: корень игрока или риг камеры. Скрипт возвращает его так, " +
             "чтобы камера оказалась в начале координат. Пусто — двигается сама камера.")]
    public Transform movingRoot;

    [Tooltip("Корневой объект со всем, что стоит на планете: рельеф, постройки, объекты. " +
             "Он едет вместе с планетой. Пусто — на планете нет ничего, кроме неба и облаков.")]
    public Transform planetContent;

    [Tooltip("На сколько метров камере можно отойти от начала координат, прежде чем мир сдвинется. " +
             "0 — каждый кадр, камера не двигается никогда. Больше 0 — реже: полезно, если на планете " +
             "много физики, потому что сдвиг статических коллайдеров каждый кадр стоит дорого.")]
    [Min(0f)] public float recenterDistanceM = 0f;

    // ==================================================================
    // Состояние

    // Насколько камера на самом деле «улетела» от старта, метры. В double: на тысячах километров
    // float теряет метры. Центр планеты — это начальный центр (0, -радиус, 0) минус этот путь.
    double _travelX, _travelY, _travelZ;

    VisualEnvironment _environment;   // из копии профиля skyVolume

    /// <summary>Центр планеты в мире, метры. В начале (0, -радиус, 0); дальше уезжает от камеры.</summary>
    public Vector3 Center =>
        new Vector3((float)-_travelX, (float)(-radiusKm * 1000.0 - _travelY), (float)-_travelZ);

    /// <summary>Сколько камера пролетела от старта, км (для отладки).</summary>
    public Vector3 TravelKm =>
        new Vector3((float)(_travelX / 1000.0), (float)(_travelY / 1000.0), (float)(_travelZ / 1000.0));

    // ==================================================================

    void Start()
    {
        if (cameraTransform == null && Camera.main != null) cameraTransform = Camera.main.transform;
        if (cameraTransform == null)
            Debug.LogWarning("[FloatingPlanet] камера не найдена — мир не сдвигается", this);

        SetupVisualEnvironment();
    }

    // В LateUpdate: контроллер уже подвинул камеру за этот кадр, а рендер ещё не начался.
    void LateUpdate()
    {
        MoveWorldInsteadOfCamera();
        UpdateVisualEnvironment();
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

    /// <summary>
    /// Возвращает камеру в начало координат, а её сдвиг переносит на планету: добавляет его к пройденному
    /// пути (из него считается Center) и сдвигает на столько же содержимое планеты. Всё вокруг камеры
    /// остаётся на своих местах относительно неё — ни глаз, ни HDRP разницы не видят.
    /// </summary>
    void MoveWorldInsteadOfCamera()
    {
        if (cameraTransform == null) return;

        Vector3 shift = cameraTransform.position;   // где камера сейчас; должна быть в (0, 0, 0)
        if (shift == Vector3.zero || shift.sqrMagnitude < recenterDistanceM * recenterDistanceM) return;

        Transform root = movingRoot != null ? movingRoot : cameraTransform;
        root.position -= shift;
        var body = root.GetComponent<Rigidbody>();
        if (body != null) body.position -= shift;   // физическое тело двигаем вместе с трансформом

        if (planetContent != null) planetContent.position -= shift;
        Physics.SyncTransforms();   // чтобы CharacterController и коллайдеры сразу узнали новые позиции

        _travelX += shift.x;
        _travelY += shift.y;
        _travelZ += shift.z;
    }
}

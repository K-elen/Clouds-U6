using UnityEngine;
#if ENABLE_INPUT_SYSTEM && !ENABLE_LEGACY_INPUT_MANAGER
using UnityEngine.InputSystem;
#endif

// То же управление, что у PlanetFlyCamera, но камера никуда не летит — движется планета (FloatingPlanet.Fly):
// ПКМ — осмотреться, WASD — лететь, E/Q — вверх/вниз, колесо мыши — скорость, Shift — в 10 раз быстрее.
// Осматривается сама камера: небо, солнце и облака HDRP привязаны к осям мира, поэтому вращать вместо
// камеры весь мир нельзя. Точности поворот камеры не вредит — вредит только большое расстояние.
public class PlanetFlyController : MonoBehaviour
{
    FloatingPlanet planet;   // находится в сцене сама

    float speed = 20f;
    float yaw, pitch;

    void Start()
    {
        planet = FindFirstObjectByType<FloatingPlanet>();
        if (planet == null) Debug.LogWarning("[PlanetFlyController] в сцене нет FloatingPlanet — лететь нечем", this);

        // берём текущий поворот камеры, чтобы при запуске ничего не дёрнулось
        Vector3 e = transform.eulerAngles;
        yaw = e.y;
        pitch = e.x > 180f ? e.x - 360f : e.x;
    }

    void Update()
    {
        bool look = MouseRight();
        Cursor.lockState = look ? CursorLockMode.Locked : CursorLockMode.None;
        if (look)
        {
            Vector2 d = MouseDelta() * 0.1f;
            yaw += d.x;
            pitch = Mathf.Clamp(pitch - d.y, -89f, 89f);
            transform.rotation = Quaternion.Euler(pitch, yaw, 0f);
        }

        float scroll = Scroll();
        if (scroll != 0f)
            speed = Mathf.Clamp(speed * (scroll > 0f ? 1.5f : 1f / 1.5f), 1f, 1000000f);

        Vector3 move = transform.forward * (Held(KeyW) - Held(KeyS))
                     + transform.right * (Held(KeyD) - Held(KeyA))
                     + Vector3.up * (Held(KeyE) - Held(KeyQ));

        float boost = Held(KeyShift) > 0f ? 10f : 1f;
        Vector3 step = Vector3.ClampMagnitude(move, 1f) * speed * boost * Time.deltaTime;

        // вместо transform.position += step — планета едет навстречу
        if (planet != null && step != Vector3.zero) planet.Fly(step);
    }

    // --- ввод ---
    // Старая система ввода (Input), если она включена (Active Input Handling = Input Manager или Both):
    // она есть в любом проекте. Новая (Input System) — только если старая выключена; тогда в проекте
    // должен стоять пакет Input System, а если скрипт лежит под Assembly Definition — в нём должна быть
    // ссылка на Unity.InputSystem.
#if ENABLE_INPUT_SYSTEM && !ENABLE_LEGACY_INPUT_MANAGER
    const Key KeyW = Key.W, KeyS = Key.S, KeyA = Key.A, KeyD = Key.D,
              KeyE = Key.E, KeyQ = Key.Q, KeyShift = Key.LeftShift;
    static float Held(Key k) => Keyboard.current[k].isPressed ? 1f : 0f;
    static bool MouseRight() => Mouse.current.rightButton.isPressed;
    static Vector2 MouseDelta() => Mouse.current.delta.ReadValue();
    static float Scroll() => Mouse.current.scroll.ReadValue().y;
#else
    const KeyCode KeyW = KeyCode.W, KeyS = KeyCode.S, KeyA = KeyCode.A, KeyD = KeyCode.D,
                  KeyE = KeyCode.E, KeyQ = KeyCode.Q, KeyShift = KeyCode.LeftShift;
    static float Held(KeyCode k) => Input.GetKey(k) ? 1f : 0f;
    static bool MouseRight() => Input.GetMouseButton(1);
    static Vector2 MouseDelta() => new Vector2(Input.GetAxisRaw("Mouse X"), Input.GetAxisRaw("Mouse Y")) * 10f;
    static float Scroll() => Input.mouseScrollDelta.y;
#endif
}

using System;
using System.Collections;
using UnityEngine;
using UnityEngine.InputSystem;

[RequireComponent(typeof(Rigidbody))]
public class Un : MonoBehaviour, RawInput.IPlayerActions
{
    private enum StateMachine
    {
        Floor, // Contains state in air
        Wall,
        Ceil
    }

    private Rigidbody _rb;
    private Collider _collider;
    private StateMachine _stateMachine = StateMachine.Floor;
    private RawInput _rawInput;
    private RawInput.PlayerActions _playerActions;
    private float _xRotation = 0f; // 카메라 상하 회전 누적값

    [SerializeField] private float moveSpeed = 5f;
    [SerializeField] private float jumpForce = 5f;
    [SerializeField] private float rayMaxDistance = 5f;
    [SerializeField] private float gravity = 9.81f;
    [SerializeField] private LayerMask wallLayerMask = ~0;
    [SerializeField] private LayerMask groundLayerMask = ~0;
    [SerializeField] private float groundCheckDistance = 0.2f;

    private Vector2 _moveInput;
    private Vector3 _wallFaceNormal;
    private bool _isClicking = false;
    private float _verticalVelocity = 0f;
    private bool _isGrounded = false;
    private float _jumpBufferTimer = 0f;
    private Coroutine _wallCheckCoroutine;

    [SerializeField] private Camera cam;
    [SerializeField] private float mouseSensitivity = 0.1f;
    [SerializeField] private float minPitch = -80f; // 아래쪽 최대 각도
    [SerializeField] private float maxPitch = 80f; // 위쪽 최대 각도
    [SerializeField] private Transform headTransform;


    private void Awake()
    {
        _rawInput = new RawInput();
        _playerActions = _rawInput.Player;
        _playerActions.AddCallbacks(this);

        TryGetComponent(out _rb);
        if (!_rb)
        {
            _rb = gameObject.AddComponent<Rigidbody>();
        }

        TryGetComponent(out _collider);

        _rb.useGravity = false;
        _rb.freezeRotation = true;
        _rb.interpolation = RigidbodyInterpolation.Interpolate;

        if (cam == null)
        {
            cam = GetComponentInChildren<Camera>();
            if (cam == null)
            {
                cam = Camera.main;
            }
        }
    }

    private void Start()
    {
        Cursor.lockState = CursorLockMode.Locked;
        Cursor.visible = false;
    }

    private void OnDestroy()
    {
        _rawInput.Dispose();
    }

    void OnEnable()
    {
        _playerActions.Enable();
    }

    void OnDisable()
    {
        _playerActions.Disable();
        if (_wallCheckCoroutine != null)
        {
            StopCoroutine(_wallCheckCoroutine);
            _wallCheckCoroutine = null;
        }
    }

    private void FixedUpdate()
    {
        switch (_stateMachine)
        {
            case StateMachine.Floor:
                FloorMove();
                break;
            case StateMachine.Wall:
                // TODO: 벽 이동 로직 작성 공간
                break;
            case StateMachine.Ceil:
                break;
            default:
                throw new ArgumentOutOfRangeException();
        }
    }

    public void OnMove(InputAction.CallbackContext context)
    {
        _moveInput = context.ReadValue<Vector2>();
    }

    private void FloorMove()
    {
        float checkDist = _collider != null ? _collider.bounds.extents.y + 0.1f : groundCheckDistance;
        _isGrounded = Physics.Raycast(transform.position, Vector3.down, checkDist, groundLayerMask);

        if (_jumpBufferTimer > 0f)
        {
            _jumpBufferTimer -= Time.fixedDeltaTime;
        }

        // 평지 중력 및 점프 적용
        if (_isGrounded)
        {
            if (_jumpBufferTimer > 0f)
            {
                _verticalVelocity = jumpForce;
                _jumpBufferTimer = 0f;
                _isGrounded = false;
            }
            else if (_verticalVelocity < 0f)
            {
                _verticalVelocity = -2f; // 접지 유지를 위한 하향 힘
            }
        }
        else
        {
            _verticalVelocity -= gravity * Time.fixedDeltaTime;
        }

        Vector3 move = (transform.right * _moveInput.x + transform.forward * _moveInput.y) * moveSpeed;
        move.y = _verticalVelocity;

        _rb.velocity = move;
    }

    public void OnJump(InputAction.CallbackContext context)
    {
        if (context.started || context.performed)
        {
            _jumpBufferTimer = 0.2f;
        }
    }

    public void OnClick(InputAction.CallbackContext context)
    {
        if (context.started || context.performed)
        {
            _isClicking = true;
            if (_wallCheckCoroutine == null)
            {
                _wallCheckCoroutine = StartCoroutine(WallCheckRoutine());
            }
        }
        else if (context.canceled)
        {
            _isClicking = false;
            if (_wallCheckCoroutine != null)
            {
                StopCoroutine(_wallCheckCoroutine);
                _wallCheckCoroutine = null;
            }
            _stateMachine = StateMachine.Floor;
            _verticalVelocity = 0f;
        }
    }

    private IEnumerator WallCheckRoutine()
    {
        Transform rayOrigin = headTransform != null ? headTransform : transform;

        while (_isClicking)
        {
            Ray ray = new Ray(rayOrigin.position, rayOrigin.forward);
            Debug.DrawRay(ray.origin, ray.direction * rayMaxDistance, Color.red);
            if (Physics.Raycast(ray, out RaycastHit hit, rayMaxDistance, wallLayerMask))
            {
                _stateMachine = StateMachine.Wall;
                _wallFaceNormal = hit.normal;
            }
            else
            {
                _stateMachine = StateMachine.Floor;
            }

            yield return null;
        }

        _stateMachine = StateMachine.Floor;
        _wallCheckCoroutine = null;
    }

    public void OnLook(InputAction.CallbackContext context)
    {
        Vector2 mouseDelta = context.ReadValue<Vector2>() * mouseSensitivity;
        float mouseX = mouseDelta.x;
        float mouseY = mouseDelta.y;
        _xRotation -= mouseY;
        _xRotation = Mathf.Clamp(_xRotation, minPitch, maxPitch);
        if (cam != null && headTransform != null)
        {
            headTransform.localRotation = Quaternion.Euler(_xRotation, 0f, 0f);
        }
        
        if (_stateMachine == StateMachine.Floor)
        {
            transform.Rotate(Vector3.up * mouseX);
        }
        else if (headTransform != null)
        {
            headTransform.Rotate(Vector3.up * mouseX);
        }
    }

    [Header("Scroll / Zoom Settings")] [SerializeField]
    private float scrollSensitivity = 0.01f;

    [SerializeField] private float minDistance = -10f;
    [SerializeField] private float maxDistance; // = 0f;

    public void OnScroll(InputAction.CallbackContext context)
    {
        if (cam == null) return;

        float scrollValue = context.ReadValue<float>();
        Vector3 currentPos = cam.transform.localPosition;
        float newZ = currentPos.z + scrollValue * scrollSensitivity;
        newZ = Mathf.Clamp(newZ, minDistance, maxDistance);

        cam.transform.localPosition = new Vector3(currentPos.x, currentPos.y, newZ);
    }
}

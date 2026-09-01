using System;
using System.Collections;
using UnityEngine;
using UnityEngine.InputSystem;

public class Un : MonoBehaviour, RawInput.IPlayerActions
{
    private enum StateMachine
    {
        Floor, //Contains state in air
        Wall,
        Ceil
    }

    private CharacterController _characterController;
    private StateMachine _stateMachine = StateMachine.Floor;
    private RawInput _rawInput;
    private RawInput.PlayerActions _playerActions;
    private float _xRotation = 0f; // 카메라 상하 회전 누적값

    [SerializeField] private float moveSpeed = 5f;
    [SerializeField] private float rayMaxDistance = 5f;
    [SerializeField] private float gravity = 9.81f;
    [SerializeField] private LayerMask wallLayerMask = ~0;

    private Vector2 _moveInput;
    private Vector3 _wallFaceNormal;
    private bool _isClicking = false;
    private float _verticalVelocity = 0f;
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
        TryGetComponent(out _characterController);

        if (!_characterController)
        {
            Destroy(this);
        }

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

    private void Update()
    {
        switch (_stateMachine)
        {
            case StateMachine.Floor:
                FloorMove();
                break;
            case StateMachine.Wall:
                WallMove();
                break;
            case StateMachine.Ceil:
                CeilMove();
                break;
            default:
                throw new ArgumentOutOfRangeException();
        }
    }

    public void OnMove(InputAction.CallbackContext context)
    {
        _moveInput = context.ReadValue<Vector2>();
    }

    private void CeilMove()
    {
        throw new NotImplementedException();
    }

    private void WallMove()
    {
        // 이동 입력이 있을 때마다 _wallFaceNormal 방향(벽 쪽)으로 레이를 쏴서 벽 법선 재계산 후 업데이트
        if (_moveInput.sqrMagnitude > 0.001f)
        {
            Ray ray = new Ray(transform.position, -_wallFaceNormal);
            if (Physics.Raycast(ray, out RaycastHit hit, rayMaxDistance, wallLayerMask))
            {
                _wallFaceNormal = hit.normal;
            }
        }

        // 벽 상방(Up) 벡터 계산: 세계 상방(Vector3.up)을 벽 면으로 투영
        Vector3 wallUp = Vector3.ProjectOnPlane(Vector3.up, _wallFaceNormal).normalized;
        if (wallUp.sqrMagnitude < 0.001f)
        {
            wallUp = Vector3.ProjectOnPlane(transform.forward, _wallFaceNormal).normalized;
        }
        Debug.DrawRay(transform.position, wallUp * rayMaxDistance, Color.red);

        // 벽 우방(Right) 벡터 계산: 벽 법선과 벽 상방 벡터의 외적
        Vector3 wallRight = Vector3.Cross(_wallFaceNormal, wallUp).normalized;

        // 중력: 벽 법선의 반대 방향(-_wallFaceNormal)으로 적용
        Vector3 wallGravity = -_wallFaceNormal * gravity;

        // 플레이어 앞/뒤 입력 -> 위/아래 방향, 플레이어 좌/우 입력 -> 좌/우 방향
        Vector3 inputMove = (wallUp * _moveInput.y + wallRight * _moveInput.x) * moveSpeed;

        Vector3 finalVelocity = inputMove + wallGravity;
        _characterController.Move(finalVelocity * Time.deltaTime);
    }

    private void FloorMove()
    {
        // 평지 중력 적용
        if (_characterController.isGrounded)
        {
            _verticalVelocity = -2f; // 접지 유지를 위한 하향 힘
        }
        else
        {
            _verticalVelocity -= gravity * Time.deltaTime;
        }

        Vector3 move = (transform.right * _moveInput.x + transform.forward * _moveInput.y) * moveSpeed;
        move.y += _verticalVelocity;

        _characterController.Move(move * Time.deltaTime);
    }

    public void OnJump(InputAction.CallbackContext context)
    {
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
        if (cam != null)
        {
            headTransform.localRotation = Quaternion.Euler(_xRotation, 0f, 0f);
        }

        transform.Rotate(Vector3.up * mouseX);
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
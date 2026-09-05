using System;
using System.Collections;
using UnityEngine;
using UnityEngine.InputSystem;

[RequireComponent(typeof(Rigidbody))]
public class Un : MonoBehaviour, RawInput.IPlayerActions
{
    private enum StateMachine
    {
        Floor, // 0~45도: 평지/완만한 경사 (점프/중력 적용)
        Wall // 45~180도: 수직벽/오버행/천장/기둥 (등반)
    }

    private Rigidbody _rb;
    public Collider _collider;
    private StateMachine _stateMachine = StateMachine.Floor;
    private RawInput _rawInput;
    private RawInput.PlayerActions _playerActions;

    [SerializeField] private float moveSpeed = 5f;
    [SerializeField] private float jumpForce = 5f;
    [SerializeField] private float rayMaxDistance = 5f;
    [SerializeField] private float gravity = 9.81f;
    [SerializeField] private LayerMask wallLayerMask = ~0;
    [SerializeField] private LayerMask groundLayerMask = ~0;
    [SerializeField] private float groundCheckDistance = 0.2f;

    [Header("Climb Shared Settings")] [SerializeField]
    private Animator animator;

    private Vector2 _moveInput;
    private Vector3 _surfaceNormal = Vector3.up;
    private Vector3 _surfaceHitPoint = Vector3.zero;
    private bool _isClicking = false;
    private float _verticalVelocity = 0f;
    private bool _isGrounded = false;
    private float _jumpBufferTimer = 0f;
    private Coroutine _wallCheckCoroutine;

    [Header("Camera & Head Tracking")] [SerializeField]
    private Camera cam;

    [SerializeField] private Transform headTransform; // 머리 뼈대 (Bone)
    [SerializeField] private Transform camPivot; // 카메라 피벗 (비어있으면 자동 생성)
    [SerializeField] private Vector3 cameraEyeOffset = new Vector3(0f, 0.1f, 0f); // 머리 뼈 기준 눈높이 오프셋
    [SerializeField] private float mouseSensitivity = 0.1f;
    [SerializeField] private float minPitch = -80f; // 아래쪽 최대 각도
    [SerializeField] private float maxPitch = 80f; // 위쪽 최대 각도

    [Header("Wall Climbing")] [SerializeField]
    private Transform leftUpperArm;

    [SerializeField] private Transform rightUpperArm;

    [Header("Scroll / Zoom Settings")] [SerializeField]
    private float scrollSensitivity = 0.01f;

    [SerializeField] private float minDistance = -10f;
    [SerializeField] private float maxDistance = 0f;
    private float _currentCamDistance = 0f;

    private float _xRotation = 0f; // 카메라 상하 각도 (Pitch)
    private float _climbYaw = 0f; // 등반 시 카메라 좌우 둘러보기 각도 (Yaw)


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

        if (!_collider)
            TryGetComponent(out _collider);

        if (animator == null)
        {
            animator = GetComponentInChildren<Animator>();
        }

        _rb.useGravity = false;
        _rb.freezeRotation = true;
        _rb.interpolation = RigidbodyInterpolation.Interpolate;

        // 카메라 설정
        if (cam == null)
        {
            cam = GetComponentInChildren<Camera>();
            if (cam == null) cam = Camera.main;
        }

        // 머리 뼈 Transform 자동 탐색 (Humanoid Avatar 기준)
        if (headTransform == null && animator != null && animator.isHuman)
        {
            headTransform = animator.GetBoneTransform(HumanBodyBones.Head);
        }

        // 독립 카메라 피벗 생성 (뼈대의 90도 왜곡 축 영향 완전 차단)
        if (camPivot == null)
        {
            GameObject pivotGo = new GameObject("CameraPivot");
            camPivot = pivotGo.transform;
            camPivot.position = headTransform != null ? headTransform.position : transform.position + Vector3.up * 1.5f;
            camPivot.rotation = transform.rotation;
        }

        if (cam != null)
        {
            // 카메라는 피벗의 자식으로 배치
            cam.transform.SetParent(camPivot, false);
            _currentCamDistance = Mathf.Clamp(cam.transform.localPosition.z, minDistance, maxDistance);
            cam.transform.localPosition = new Vector3(0f, 0f, _currentCamDistance);
            cam.transform.localRotation = Quaternion.identity;
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
                break;
            default:
                throw new ArgumentOutOfRangeException();
        }
    }

    /// <summary>
    /// 모든 애니메이션 및 IK가 완료된 후, 카메라 피벗이 머리 뼈 위치를 정확히 추적하도록 동기화합니다.
    /// 뼈의 왜곡된 회전값은 무시하고, 순수한 카메라 시선 회전값만 적용합니다.
    /// </summary>
    private void LateUpdate()
    {
        if (camPivot == null) return;

        // 1. 머리 뼈 위치 실시간 추적 (애니메이션에 맞춰 머리가 움직여도 정확히 따라감)
        Vector3 targetPivotPos = headTransform != null
            ? headTransform.position + transform.TransformDirection(cameraEyeOffset)
            : transform.position + Vector3.up * 1.5f;
        camPivot.position = targetPivotPos;

        // 2. 카메라 회전 적용 (뼈대의 90도 회전축을 타지 않고 똑바로 회전)
        if (_stateMachine == StateMachine.Floor)
        {
            // 지상에서는 몸통의 Y축 회전 + 카메라 상하(Pitch) 회전
            camPivot.rotation = Quaternion.Euler(_xRotation, transform.eulerAngles.y, 0f);
        }
        else
        {
            // 등반 중에는 몸통 기준 상대적 시점 둘러보기
            Quaternion baseRot = transform.rotation;
            camPivot.rotation = baseRot * Quaternion.Euler(_xRotation, _climbYaw, 0f);
        }
    }

    public void OnMove(InputAction.CallbackContext context)
    {
        _moveInput = context.ReadValue<Vector2>();
    }

    private void FloorMove()
    {
        // 바닥 상태에서는 항상 몸체를 월드 Up(Vector3.up) 기준으로 똑바로 정렬 유지
        Vector3 forwardOnFloor = Vector3.ProjectOnPlane(transform.forward, Vector3.up).normalized;
        if (forwardOnFloor.sqrMagnitude > 0.001f)
        {
            Quaternion targetFloorRot = Quaternion.LookRotation(forwardOnFloor, Vector3.up);
            transform.rotation = Quaternion.Slerp(transform.rotation, targetFloorRot, Time.fixedDeltaTime * 20f);
        }

        float checkDist = _collider != null ? _collider.bounds.extents.y + 0.1f : groundCheckDistance;
        _isGrounded = Physics.Raycast(transform.position, Vector3.down, checkDist, groundLayerMask);

        if (_jumpBufferTimer > 0f)
        {
            _jumpBufferTimer -= Time.fixedDeltaTime;
        }

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
                _verticalVelocity = -2f;
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

    /// <summary>
    /// Floor 상태 진입 시 몸을 즉시 월드 기준 위쪽(Vector3.up)으로 정렬하고 카메라 시선 방향을 바라보도록 설정합니다.
    /// </summary>
    private void UprightBodyOnFloor()
    {
        Vector3 fwd = Vector3.zero;
        if (cam != null)
        {
            fwd = Vector3.ProjectOnPlane(cam.transform.forward, Vector3.up).normalized;
        }

        if (fwd.sqrMagnitude < 0.001f)
        {
            fwd = Vector3.ProjectOnPlane(transform.forward, Vector3.up).normalized;
        }

        if (fwd.sqrMagnitude < 0.001f)
        {
            fwd = Vector3.ProjectOnPlane(-transform.up, Vector3.up).normalized;
        }

        if (fwd.sqrMagnitude < 0.001f)
        {
            fwd = Vector3.forward;
        }

        transform.rotation = Quaternion.LookRotation(fwd, Vector3.up);
        _climbYaw = 0f;
    }

    public void OnJump(InputAction.CallbackContext context)
    {
        if (context.started || context.performed)
        {
        }
    }

    public void OnClick(InputAction.CallbackContext context)
    {
        if (context.started || context.performed && _wallCheckCoroutine == null)
        {
            _wallCheckCoroutine = StartCoroutine(WallCheckCoroutine());
        }
        else if (context.canceled && _wallCheckCoroutine != null)
        {
            StopCoroutine(_wallCheckCoroutine);
            _wallCheckCoroutine = null;
            _stateMachine = StateMachine.Floor;
        }
    }

    private IEnumerator WallCheckCoroutine()
    {
        while (true)
        {
            Ray ray = new Ray();
            ray.origin = headTransform.position;
            ray.direction = (headTransform.forward + camPivot.forward).normalized;
            Debug.DrawRay(ray.origin, ray.direction * rayMaxDistance, Color.red);
            if (Physics.Raycast(ray, out RaycastHit hit, rayMaxDistance))
            {
                
                float angle = Vector3.Angle(Vector3.up, hit.normal);
                if (angle >= 45f)
                {
                    _stateMachine = StateMachine.Wall;
                    _surfaceNormal = hit.normal;
                    yield break;
                }
            }
            yield return null;
        }
    }


    public void OnLook(InputAction.CallbackContext context)
    {
        Vector2 mouseDelta = context.ReadValue<Vector2>() * mouseSensitivity;
        float mouseX = mouseDelta.x;
        float mouseY = mouseDelta.y;

        // 상하 각도 누적 (Pitch)
        _xRotation -= mouseY;
        _xRotation = Mathf.Clamp(_xRotation, minPitch, maxPitch);

        if (_stateMachine == StateMachine.Floor)
        {
            // 지상에서는 몸체 전체를 좌우로 회전
            transform.Rotate(Vector3.up * mouseX);
        }
        else
        {
            // 벽이나 천장에서는 몸체는 표면에 붙어있고 카메라 피벗만 둘러보기
            _climbYaw += mouseX;
        }
    }

    public void OnScroll(InputAction.CallbackContext context)
    {
        if (cam == null) return;

        float scrollValue = context.ReadValue<float>();
        _currentCamDistance += scrollValue * scrollSensitivity;
        _currentCamDistance = Mathf.Clamp(_currentCamDistance, minDistance, maxDistance);

        // 왜곡 없는 로컬 Z축 줌인/줌아웃
        cam.transform.localPosition = new Vector3(0f, 0f, _currentCamDistance);
    }
}
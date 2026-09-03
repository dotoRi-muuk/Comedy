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
        Wall,  // 45~135도: 수직벽/오버행 (벽 등반)
        Ceil   // 135~180도: 천장/루프 (매달리기)
    }

    private Rigidbody _rb;
    private Collider _collider;
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

    [Header("Wall Settings (45~135 deg)")]
    [SerializeField] private float wallDistance = 0.6f; // 벽과 유지할 거리
    [SerializeField] private float wallSnapSpeed = 15f; // 벽으로 붙는 속도
    [SerializeField] private float bodyAlignSpeed = 12f; // 벽 기울기에 몸을 맞추는 회전 속도
    [SerializeField] private float normalSmoothSpeed = 15f; // 벽 노멀 보간 속도

    [Header("Ceil Settings (135~180 deg)")]
    [SerializeField] private float ceilHangDistance = 1.3f; // 천장 표면에서 몸 중심까지의 매달림 거리
    [SerializeField] private float ceilStepDistance = 0.45f; // 천장 한 걸음 이동 보폭
    [SerializeField] private float ceilReachDistance = 0.6f; // 천장 손 뻗는 거리
    [SerializeField] private float ceilArcDip = 0.15f; // 천장 손 이동 시 아래로 살짝 내리는 호 깊이
    [SerializeField] private float ceilBodyAlignSpeed = 8f; // 천장에서 시선 방향 정렬 속도

    [Header("Climb Shared Settings")]
    [SerializeField] private Animator animator;
    [SerializeField] private float climbStepDistance = 0.45f; // 벽 보폭
    [SerializeField] private float handReachDistance = 0.65f; // 벽 손 뻗는 거리
    [SerializeField] private float shoulderSpread = 0.3f; // 양손 좌우 기본 간격
    [SerializeField] private float handHeightOffset = 0.2f; // 벽 평상시 손 높이
    [SerializeField] private float handMoveDuration = 0.18f; // 손 뻗는 시간(초)
    [SerializeField] private float bodyMoveDuration = 0.15f; // 몸 끌어올리는 시간(초)
    [SerializeField] private float arcNormalOffset = 0.15f; // 벽 호 돌출: 벽 바깥쪽
    [SerializeField] private float arcSideOffset = 0.12f; // 호 돌출: 양옆 바깥쪽
    [SerializeField] private float arcUpOffset = 0.05f; // 호 돌출: 진행축 위쪽

    private Vector2 _moveInput;
    private Vector3 _surfaceNormal = Vector3.up;
    private Vector3 _surfaceHitPoint = Vector3.zero;
    private bool _isClicking = false;
    private float _verticalVelocity = 0f;
    private bool _isGrounded = false;
    private float _jumpBufferTimer = 0f;
    private Coroutine _wallCheckCoroutine;

    // IK 및 등반 상태 변수
    private Vector3 _leftHandPos;
    private Vector3 _rightHandPos;
    private float _leftHandWeight = 0f;
    private float _rightHandWeight = 0f;
    private bool _isRightHandTurn = true;
    private bool _isClimbingStep = false;
    private Coroutine _climbRoutine;

    [Header("Camera & Head Tracking")]
    [SerializeField] private Camera cam;
    [SerializeField] private Transform headTransform; // 머리 뼈대 (Bone)
    [SerializeField] private Transform camPivot; // 카메라 피벗 (비어있으면 자동 생성)
    [SerializeField] private Vector3 cameraEyeOffset = new Vector3(0f, 0.1f, 0f); // 머리 뼈 기준 눈높이 오프셋
    [SerializeField] private float mouseSensitivity = 0.1f;
    [SerializeField] private float minPitch = -80f; // 아래쪽 최대 각도
    [SerializeField] private float maxPitch = 80f; // 위쪽 최대 각도

    [Header("Scroll / Zoom Settings")]
    [SerializeField] private float scrollSensitivity = 0.01f;
    [SerializeField] private float minDistance = -10f;
    [SerializeField] private float maxDistance = 0f;
    private float _currentCamDistance = 0f;

    private float _xRotation = 0f; // 카메라 상하 각도 (Pitch)
    private float _climbYaw = 0f;  // 벽/천장 매달리기 시 카메라 좌우 둘러보기 각도 (Yaw)


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
        if (_climbRoutine != null)
        {
            StopCoroutine(_climbRoutine);
            _climbRoutine = null;
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
                UpdateWallAlignment();
                WallHold();
                CheckClimbInput();
                break;
            case StateMachine.Ceil:
                UpdateCeilAlignment();
                CeilHold();
                CheckCeilClimbInput();
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
            // 벽/천장에서는 몸통 기준 상대적 시점 둘러보기
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

    #region 표면 각도 판정 및 상태 전환

    private StateMachine EvaluateSurfaceState(Vector3 normal)
    {
        float slopeAngle = Vector3.Angle(Vector3.up, normal);
        if (slopeAngle <= 45f) return StateMachine.Floor;
        if (slopeAngle <= 135f) return StateMachine.Wall;
        return StateMachine.Ceil;
    }

    private void SwitchSurfaceState(StateMachine newState, RaycastHit hit)
    {
        if (_stateMachine == newState) return;

        if (_climbRoutine != null)
        {
            StopCoroutine(_climbRoutine);
            _climbRoutine = null;
        }
        _isClimbingStep = false;
        _stateMachine = newState;
        _surfaceNormal = hit.normal;
        _surfaceHitPoint = hit.point;
        _rb.velocity = Vector3.zero;
        _verticalVelocity = 0f;
        _climbYaw = 0f; // 매달리기 진입 시 시선 초기화

        switch (_stateMachine)
        {
            case StateMachine.Wall:
                InitializeWallClimb();
                break;
            case StateMachine.Ceil:
                InitializeCeilHang();
                break;
            case StateMachine.Floor:
                _leftHandWeight = 0f;
                _rightHandWeight = 0f;
                break;
        }
    }

    #endregion

    #region Wall (벽 등반) 로직

    private void UpdateWallAlignment()
    {
        if (_surfaceNormal == Vector3.zero) return;

        Vector3 rayOrigin = transform.position + transform.up * handHeightOffset;
        Ray ray = new Ray(rayOrigin, -_surfaceNormal);

        if (Physics.Raycast(ray, out RaycastHit hit, rayMaxDistance * 2f, wallLayerMask))
        {
            StateMachine evaluated = EvaluateSurfaceState(hit.normal);
            if (evaluated != StateMachine.Wall)
            {
                SwitchSurfaceState(evaluated, hit);
                return;
            }

            _surfaceNormal = Vector3.Slerp(_surfaceNormal, hit.normal, Time.fixedDeltaTime * normalSmoothSpeed);
            _surfaceHitPoint = hit.point;
        }

        Vector3 wallUp = Vector3.ProjectOnPlane(Vector3.up, _surfaceNormal).normalized;
        if (wallUp.sqrMagnitude < 0.001f) wallUp = Vector3.ProjectOnPlane(transform.up, _surfaceNormal).normalized;

        Quaternion targetRotation = Quaternion.LookRotation(-_surfaceNormal, wallUp);
        transform.rotation = Quaternion.Slerp(transform.rotation, targetRotation, Time.fixedDeltaTime * bodyAlignSpeed);
    }

    private void WallHold()
    {
        _rb.velocity = Vector3.zero;
        _verticalVelocity = 0f;

        if (_isClimbingStep || _surfaceNormal == Vector3.zero) return;

        Vector3 rayOrigin = transform.position + transform.up * handHeightOffset;
        Ray ray = new Ray(rayOrigin, -_surfaceNormal);
        if (Physics.Raycast(ray, out RaycastHit hit, rayMaxDistance * 2f, wallLayerMask))
        {
            Vector3 currentPos = transform.position;
            Vector3 targetPos = currentPos + hit.normal * (wallDistance - hit.distance);
            _rb.MovePosition(Vector3.Lerp(currentPos, targetPos, Time.fixedDeltaTime * wallSnapSpeed));
        }
    }

    private void InitializeWallClimb()
    {
        Vector3 wallUp = Vector3.ProjectOnPlane(Vector3.up, _surfaceNormal).normalized;
        if (wallUp.sqrMagnitude < 0.001f) wallUp = transform.up;
        Vector3 wallRight = Vector3.Cross(_surfaceNormal, wallUp).normalized;

        transform.rotation = Quaternion.LookRotation(-_surfaceNormal, wallUp);

        Vector3 chestPos = transform.position + wallUp * handHeightOffset;
        _leftHandPos = FindSurfacePoint(chestPos - wallRight * shoulderSpread, -_surfaceNormal);
        _rightHandPos = FindSurfacePoint(chestPos + wallRight * shoulderSpread, -_surfaceNormal);

        _leftHandWeight = 1f;
        _rightHandWeight = 1f;
        _isRightHandTurn = true;
        _isClimbingStep = false;
    }

    private void CheckClimbInput()
    {
        if (_isClimbingStep) return;

        if (_moveInput.sqrMagnitude > 0.05f)
        {
            if (_climbRoutine != null) StopCoroutine(_climbRoutine);
            _climbRoutine = StartCoroutine(ClimbStepRoutine());
        }
    }

    private IEnumerator ClimbStepRoutine()
    {
        _isClimbingStep = true;

        Vector3 wallUp = Vector3.ProjectOnPlane(Vector3.up, _surfaceNormal).normalized;
        if (wallUp.sqrMagnitude < 0.001f) wallUp = transform.up;
        Vector3 wallRight = Vector3.Cross(_surfaceNormal, wallUp).normalized;

        Vector2 inputDir = _moveInput.normalized;
        Vector3 climbDir = (wallRight * inputDir.x + wallUp * inputDir.y).normalized;

        bool movingRightHand = _isRightHandTurn;
        Vector3 startHandPos = movingRightHand ? _rightHandPos : _leftHandPos;
        float sideSign = movingRightHand ? 1f : -1f;

        Vector3 shoulderBase = transform.position + wallUp * handHeightOffset + (wallRight * (shoulderSpread * sideSign));
        Vector3 rayOrigin = shoulderBase + climbDir * (climbStepDistance * 0.5f);

        Vector3 targetHandPos;
        Ray handRay = new Ray(rayOrigin + _surfaceNormal * 0.2f, -_surfaceNormal);
        if (Physics.Raycast(handRay, out RaycastHit hit, rayMaxDistance, wallLayerMask))
        {
            targetHandPos = hit.point;
        }
        else
        {
            targetHandPos = startHandPos + climbDir * handReachDistance;
        }

        Vector3 midPoint = (startHandPos + targetHandPos) * 0.5f;
        Vector3 controlPoint = midPoint
                               + _surfaceNormal * arcNormalOffset
                               + (wallRight * (sideSign * arcSideOffset))
                               + (climbDir * arcUpOffset);

        float elapsed = 0f;
        while (elapsed < handMoveDuration)
        {
            elapsed += Time.fixedDeltaTime;
            float t = Mathf.Clamp01(elapsed / handMoveDuration);
            float easeT = 1f - (1f - t) * (1f - t);

            Vector3 currentPos = CalculateBezierPoint(easeT, startHandPos, controlPoint, targetHandPos);
            if (movingRightHand) _rightHandPos = currentPos;
            else _leftHandPos = currentPos;

            yield return new WaitForFixedUpdate();
        }

        if (movingRightHand) _rightHandPos = targetHandPos;
        else _leftHandPos = targetHandPos;

        Vector3 startBodyPos = transform.position;
        Vector3 targetBodyPos = startBodyPos + climbDir * climbStepDistance;

        elapsed = 0f;
        while (elapsed < bodyMoveDuration)
        {
            elapsed += Time.fixedDeltaTime;
            float t = Mathf.Clamp01(elapsed / bodyMoveDuration);
            float easeT = t * t * (3f - 2f * t);

            _rb.MovePosition(Vector3.Lerp(startBodyPos, targetBodyPos, easeT));
            yield return new WaitForFixedUpdate();
        }

        _isRightHandTurn = !_isRightHandTurn;
        _isClimbingStep = false;
        _climbRoutine = null;
    }

    #endregion

    #region Ceil (천장 매달리기) 로직

    private void UpdateCeilAlignment()
    {
        if (_surfaceNormal == Vector3.zero) return;

        Ray ray = new Ray(transform.position, -_surfaceNormal);
        if (Physics.Raycast(ray, out RaycastHit hit, rayMaxDistance * 2f, wallLayerMask))
        {
            StateMachine evaluated = EvaluateSurfaceState(hit.normal);
            if (evaluated != StateMachine.Ceil)
            {
                SwitchSurfaceState(evaluated, hit);
                return;
            }

            _surfaceNormal = Vector3.Slerp(_surfaceNormal, hit.normal, Time.fixedDeltaTime * normalSmoothSpeed);
            _surfaceHitPoint = hit.point;
        }

        Vector3 ceilUp = -_surfaceNormal;
        Vector3 camForwardOnCeil = Vector3.ProjectOnPlane(cam.transform.forward, ceilUp).normalized;
        if (camForwardOnCeil.sqrMagnitude < 0.001f) camForwardOnCeil = transform.forward;

        Quaternion targetRot = Quaternion.LookRotation(camForwardOnCeil, ceilUp);
        transform.rotation = Quaternion.Slerp(transform.rotation, targetRot, Time.fixedDeltaTime * ceilBodyAlignSpeed);
    }

    private void CeilHold()
    {
        _rb.velocity = Vector3.zero;
        _verticalVelocity = 0f;

        if (_isClimbingStep || _surfaceNormal == Vector3.zero) return;

        Ray ray = new Ray(transform.position, -_surfaceNormal);
        if (Physics.Raycast(ray, out RaycastHit hit, rayMaxDistance * 2f, wallLayerMask))
        {
            Vector3 currentPos = transform.position;
            Vector3 targetPos = hit.point + _surfaceNormal * ceilHangDistance;
            _rb.MovePosition(Vector3.Lerp(currentPos, targetPos, Time.fixedDeltaTime * wallSnapSpeed));
        }
    }

    private void InitializeCeilHang()
    {
        Vector3 ceilUp = -_surfaceNormal;
        Vector3 camForward = Vector3.ProjectOnPlane(cam.transform.forward, ceilUp).normalized;
        if (camForward.sqrMagnitude < 0.001f) camForward = transform.forward;
        transform.rotation = Quaternion.LookRotation(camForward, ceilUp);

        Vector3 headPos = transform.position + transform.up * 1.5f;
        _leftHandPos = FindSurfacePoint(headPos - transform.right * shoulderSpread, ceilUp);
        _rightHandPos = FindSurfacePoint(headPos + transform.right * shoulderSpread, ceilUp);

        _leftHandWeight = 1f;
        _rightHandWeight = 1f;
        _isRightHandTurn = true;
        _isClimbingStep = false;
    }

    private void CheckCeilClimbInput()
    {
        if (_isClimbingStep) return;

        if (_moveInput.sqrMagnitude > 0.05f)
        {
            if (_climbRoutine != null) StopCoroutine(_climbRoutine);
            _climbRoutine = StartCoroutine(CeilStepRoutine());
        }
    }

    private IEnumerator CeilStepRoutine()
    {
        _isClimbingStep = true;

        Vector3 ceilUp = -_surfaceNormal;
        Vector3 camForward = Vector3.ProjectOnPlane(cam.transform.forward, ceilUp).normalized;
        Vector3 camRight = Vector3.ProjectOnPlane(cam.transform.right, ceilUp).normalized;
        Vector3 moveDir = (camRight * _moveInput.x + camForward * _moveInput.y).normalized;

        bool movingRightHand = _isRightHandTurn;
        Vector3 startHandPos = movingRightHand ? _rightHandPos : _leftHandPos;
        float sideSign = movingRightHand ? 1f : -1f;

        Vector3 shoulderBase = transform.position + transform.up * 1.5f + (transform.right * (shoulderSpread * sideSign));
        Vector3 rayOrigin = shoulderBase + moveDir * ceilStepDistance;

        Vector3 targetHandPos;
        Ray handRay = new Ray(rayOrigin - ceilUp * 0.2f, ceilUp);
        if (Physics.Raycast(handRay, out RaycastHit hit, rayMaxDistance, wallLayerMask))
        {
            targetHandPos = hit.point;
        }
        else
        {
            targetHandPos = startHandPos + moveDir * ceilReachDistance;
        }

        Vector3 midPoint = (startHandPos + targetHandPos) * 0.5f;
        Vector3 controlPoint = midPoint
                               + _surfaceNormal * ceilArcDip
                               + (transform.right * (sideSign * arcSideOffset));

        float elapsed = 0f;
        while (elapsed < handMoveDuration)
        {
            elapsed += Time.fixedDeltaTime;
            float t = Mathf.Clamp01(elapsed / handMoveDuration);
            float easeT = 1f - (1f - t) * (1f - t);

            Vector3 currentPos = CalculateBezierPoint(easeT, startHandPos, controlPoint, targetHandPos);
            if (movingRightHand) _rightHandPos = currentPos;
            else _leftHandPos = currentPos;

            yield return new WaitForFixedUpdate();
        }

        if (movingRightHand) _rightHandPos = targetHandPos;
        else _leftHandPos = targetHandPos;

        Vector3 startBodyPos = transform.position;
        Vector3 targetBodyPos = startBodyPos + moveDir * ceilStepDistance;

        elapsed = 0f;
        while (elapsed < bodyMoveDuration)
        {
            elapsed += Time.fixedDeltaTime;
            float t = Mathf.Clamp01(elapsed / bodyMoveDuration);
            float easeT = t * t * (3f - 2f * t);

            _rb.MovePosition(Vector3.Lerp(startBodyPos, targetBodyPos, easeT));
            yield return new WaitForFixedUpdate();
        }

        _isRightHandTurn = !_isRightHandTurn;
        _isClimbingStep = false;
        _climbRoutine = null;
    }

    #endregion

    private Vector3 FindSurfacePoint(Vector3 fromPos, Vector3 castDir)
    {
        Ray ray = new Ray(fromPos, castDir);
        if (Physics.Raycast(ray, out RaycastHit hit, rayMaxDistance, wallLayerMask))
        {
            return hit.point;
        }
        return fromPos + castDir * 0.5f;
    }

    private Vector3 CalculateBezierPoint(float t, Vector3 p0, Vector3 p1, Vector3 p2)
    {
        float u = 1f - t;
        return (u * u * p0) + (2f * u * t * p1) + (t * t * p2);
    }

    private void OnAnimatorIK(int layerIndex)
    {
        if (animator == null) return;

        if (_stateMachine == StateMachine.Wall || _stateMachine == StateMachine.Ceil)
        {
            animator.SetIKPositionWeight(AvatarIKGoal.LeftHand, _leftHandWeight);
            animator.SetIKPosition(AvatarIKGoal.LeftHand, _leftHandPos);

            animator.SetIKPositionWeight(AvatarIKGoal.RightHand, _rightHandWeight);
            animator.SetIKPosition(AvatarIKGoal.RightHand, _rightHandPos);
        }
        else
        {
            animator.SetIKPositionWeight(AvatarIKGoal.LeftHand, 0f);
            animator.SetIKPositionWeight(AvatarIKGoal.RightHand, 0f);
        }
    }

    public void OnJump(InputAction.CallbackContext context)
    {
        if (context.started || context.performed)
        {
            if (_stateMachine == StateMachine.Wall || _stateMachine == StateMachine.Ceil)
            {
                ReleaseClimb();
                return;
            }
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
                _wallCheckCoroutine = StartCoroutine(CheckRoutine());
            }
        }
        else if (context.canceled)
        {
            _isClicking = false;
            ReleaseClimb();
        }
    }

    private void ReleaseClimb()
    {
        if (_wallCheckCoroutine != null)
        {
            StopCoroutine(_wallCheckCoroutine);
            _wallCheckCoroutine = null;
        }
        if (_climbRoutine != null)
        {
            StopCoroutine(_climbRoutine);
            _climbRoutine = null;
        }
        _stateMachine = StateMachine.Floor;
        _surfaceNormal = Vector3.up;
        _surfaceHitPoint = Vector3.zero;
        _verticalVelocity = 0f;
        _leftHandWeight = 0f;
        _rightHandWeight = 0f;
        _isClimbingStep = false;
        _climbYaw = 0f;
    }

    private IEnumerator CheckRoutine()
    {
        while (_isClicking)
        {
            if (_stateMachine == StateMachine.Floor)
            {
                // 피벗(눈높이) 또는 머리에서 정면으로 레이 발사
                Vector3 rayOrigin = camPivot != null ? camPivot.position : (headTransform != null ? headTransform.position : transform.position);
                Vector3 rayDir = cam != null ? cam.transform.forward : transform.forward;
                Debug.DrawRay(rayOrigin, rayDir * rayMaxDistance, Color.red);
                if (Physics.Raycast(new Ray(rayOrigin, rayDir), out RaycastHit hit, rayMaxDistance, wallLayerMask))
                {
                    StateMachine evaluated = EvaluateSurfaceState(hit.normal);
                    if (evaluated != StateMachine.Floor)
                    {
                        SwitchSurfaceState(evaluated, hit);
                    }
                }
            }

            yield return null;
        }

        ReleaseClimb();
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

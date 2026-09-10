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
    [SerializeField] private StateMachine _stateMachine = StateMachine.Floor;
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
    private Coroutine _wallClimbCoroutine;

    [Header("Camera & Head Tracking")] [SerializeField]
    private Camera cam;

    [SerializeField] private Transform headTransform; // 머리 뼈대 (Bone)
    [SerializeField] private Transform camPivot; // 카메라 피벗 (비어있으면 자동 생성)
    [SerializeField] private Vector3 cameraEyeOffset = new Vector3(0f, 0.1f, 0f); // 머리 뼈 기준 눈높이 오프셋
    [SerializeField] private float mouseSensitivity = 0.1f;
    [SerializeField] private float minPitch = -80f; // 아래쪽 최대 각도
    [SerializeField] private float maxPitch = 80f; // 위쪽 최대 각도
    [SerializeField] private float wallYawLimit = 70f; // 벽 등반 시 좌우 시야각 한계

    [Header("Wall Climbing")] [SerializeField]
    private Transform leftUpperArm;

    [SerializeField] private Transform rightUpperArm;
    [SerializeField] private Transform leftCalf;
    [SerializeField] private Transform rightCalf;
    [SerializeField] private float wallDistance = 0.5f;
    [SerializeField] private float armLength = 0.7f;
    [SerializeField] private float wallClimbDuration = 0.5f;

    [Header("Scroll / Zoom Settings")] [SerializeField]
    private float scrollSensitivity = 0.01f;

    [SerializeField] private float minDistance = -10f;
    [SerializeField] private float maxDistance = 0f;
    private float _currentCamDistance = 0f;

    // 통합 각도 상태 (Single Source of Truth)
    private float _pitch = 0f; // 상하 각도 (Pitch)
    private float _yaw = 0f; // 지상: 월드 Yaw, 벽: 벽 기준 상대 Yaw
    private bool _isRightTurn = true;
    private Vector3 _rightHandPos;
    private Vector3 _leftHandPos;
    private Vector3 _rightFootPos;
    private Vector3 _leftFootPos;
    
    
    [Header("IK Settings")]
    [Range(0f, 1f)] [SerializeField] private float handIKWeight = 1f;
    [Range(0f, 1f)] [SerializeField] private float footIKWeight = 1f;


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

        // 초기 수평 각도 설정
        _yaw = transform.eulerAngles.y;

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

        // 독립 카메라 피벗 생성 (뼈대의 회전 왜곡 영향 차단)
        if (camPivot == null)
        {
            GameObject pivotGo = new GameObject("CameraPivot");
            camPivot = pivotGo.transform;
            camPivot.position = GetCameraPivotPosition();
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
        if (_wallClimbCoroutine != null)
        {
            StopCoroutine(_wallClimbCoroutine);
            _wallClimbCoroutine = null;
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
    /// 단일 Pitch/Yaw 각도를 기준으로 몸체(Body)와 카메라 피벗(CameraPivot)의 위치/회전을 동기화합니다.
    /// </summary>
    private void LateUpdate()
    {
        if (camPivot == null) return;

        // 1. 카메라 피벗 위치 동기화 (머리 뼈 기준)
        camPivot.position = GetCameraPivotPosition();

        // 2. 상태에 따른 몸체 및 카메라 회전 동기화
        if (_stateMachine == StateMachine.Floor)
        {
            transform.rotation = Quaternion.Euler(0f, _yaw, 0f);
            camPivot.rotation = Quaternion.Euler(_pitch, _yaw, 0f);
        }
        else // Wall
        {
            // 벽면을 따라 위로 향하는 Up 벡터 계산 (경사벽/오버행 등 대응)
            Vector3 wallUp = Vector3.ProjectOnPlane(Vector3.up, _surfaceNormal).normalized;
            if (wallUp.sqrMagnitude < 0.001f)
            {
                wallUp = transform.up;
            }

            Quaternion wallBaseRot = Quaternion.LookRotation(-_surfaceNormal, wallUp);
            transform.rotation = wallBaseRot;
            camPivot.rotation = wallBaseRot * Quaternion.Euler(_pitch, _yaw, 0f);
        }
    }

    private Vector3 GetCameraPivotPosition()
    {
        return headTransform != null
            ? headTransform.position + transform.TransformDirection(cameraEyeOffset)
            : transform.position + Vector3.up * 1.5f;
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

    public void OnJump(InputAction.CallbackContext context)
    {
        if (context.started || context.performed)
        {
        }
    }

    public void OnClick(InputAction.CallbackContext context)
    {
        if (context.started || (context.performed && _wallCheckCoroutine == null))
        {
            if (_wallCheckCoroutine == null)
            {
                _wallCheckCoroutine = StartCoroutine(WallCheckCoroutine());
            }
        }
        else if (context.canceled)
        {
            if (_wallCheckCoroutine != null)
            {
                StopCoroutine(_wallCheckCoroutine);
                _wallCheckCoroutine = null;
            }

            if (_stateMachine == StateMachine.Wall)
            {
                TransitionToFloor();
            }
        }
    }

    private void TransitionToWall(Vector3 hitPoint, Vector3 hitNormal)
    {
        _surfaceNormal = hitNormal;
        _surfaceHitPoint = hitPoint;
        _stateMachine = StateMachine.Wall;

        // 벽 상태 진입 시 물리 속도 초기화 (잔여 속도로 밀려나는 현상 방지)
        _rb.velocity = Vector3.zero;
        _verticalVelocity = 0f;

        // 벽에 붙을 때 벽 기준 상대 Yaw를 0(정면)으로 초기화
        _yaw = 0f;

        Vector3 wallUp = Vector3.ProjectOnPlane(Vector3.up, hitNormal).normalized;
        if (wallUp.sqrMagnitude < 0.001f)
        {
            wallUp = Vector3.up;
        }

        transform.position = hitPoint + hitNormal * wallDistance;
        transform.rotation = Quaternion.LookRotation(-hitNormal, wallUp);
    }

    private void TransitionToFloor()
    {
        // 벽에서 지상으로 복귀 시 현재 카메라인 월드 수평각(Yaw)을 유지하여 시점 점프 방지
        Vector3 lookForward = camPivot != null ? camPivot.forward : transform.forward;
        Vector3 floorForward = Vector3.ProjectOnPlane(lookForward, Vector3.up).normalized;
        if (floorForward.sqrMagnitude > 0.001f)
        {
            _yaw = Quaternion.LookRotation(floorForward, Vector3.up).eulerAngles.y;
        }

        _stateMachine = StateMachine.Floor;
        if (_wallClimbCoroutine != null)
        {
            StopCoroutine(_wallClimbCoroutine);
            _wallClimbCoroutine = null;
        }
        transform.rotation = Quaternion.Euler(0f, _yaw, 0f);
    }

    private IEnumerator WallCheckCoroutine()
    {
        while (true)
        {
            Transform rayDirTransform = headTransform != null ? headTransform : transform;
            var ray = new Ray
            {
                origin = transform.position,
                direction = rayDirTransform.forward
            };
            Debug.DrawRay(ray.origin, ray.direction * rayMaxDistance, Color.red);
            if (Physics.Raycast(ray, out RaycastHit hit, rayMaxDistance, wallLayerMask))
            {
                _wallCheckCoroutine = null;

                if (_wallClimbCoroutine != null)
                {
                    StopCoroutine(_wallClimbCoroutine);
                }
                _wallClimbCoroutine = StartCoroutine(WallClimbCoroutine());
                yield break;
            }

            yield return null;
        }
    }

    private IEnumerator WallClimbCoroutine()
    {
        try
        {
            var ray = new Ray
            {
                origin = transform.position,
                direction = transform.forward
            };
            if (Physics.Raycast(ray, out RaycastHit transHit, rayMaxDistance, wallLayerMask))
            {
                var angle = Vector3.Angle(Vector3.up, transHit.normal);
                if (angle >= 45f)
                {
                    TransitionToWall(transHit.point, transHit.normal);
                }
            }
            Transform arm, calf;
            var upGoingDistance = 0f;

            while (_stateMachine == StateMachine.Wall)
            {
                if (_isRightTurn)
                {
                    arm = rightUpperArm;
                    calf = rightCalf;
                }
                else
                {
                    arm = leftUpperArm;
                    calf = leftCalf;
                }

                if (arm == null || calf == null)
                {
                    yield return null;
                    continue;
                }

                var armRay = new Ray
                {
                    origin = arm.position,
                    direction = transform.forward
                };
                var calfRay = new Ray
                {
                    origin = calf.position,
                    direction = transform.forward
                };
                Vector3 wallUp = Vector3.up;
                Debug.DrawRay(armRay.origin, armRay.direction * rayMaxDistance, Color.red);
                if (Physics.Raycast(armRay, out RaycastHit hit, rayMaxDistance, wallLayerMask))
                {
                    // 벽면 법선(기울기) 및 접촉점 최신화
                    _surfaceNormal = hit.normal;
                    _surfaceHitPoint = hit.point;

                    // 1. 벽면을 따라 위로 향하는 방향 벡터 계산 (경사벽/오버행 대응)
                    wallUp = Vector3.ProjectOnPlane(Vector3.up, hit.normal).normalized;
                    if (wallUp.sqrMagnitude < 0.001f) // 천장/바닥 등 수직 투영이 0일 때 대비
                    {
                        wallUp = transform.up;
                    }

                    // 2. 어깨에서 벽면까지의 '직교 최단 거리' 계산
                    var distanceToPlane = Mathf.Abs(Vector3.Dot(hit.normal, arm.position - hit.point));

                    // 3. 팔이 닿는 범위 내인지 확인 및 NaN 방지
                    if (armLength >= distanceToPlane)
                    {
                        // 벽면을 따라 팔을 뻗을 수 있는 높이차 (피타고라스)
                        var reachUpDistance = Mathf.Sqrt(armLength * armLength - distanceToPlane * distanceToPlane);

                        // 4. 어깨를 벽면에 직교 투영한 지점
                        var projectedShoulderOnWall = arm.position - hit.normal * Vector3.Dot(hit.normal, arm.position - hit.point);

                        // 5. 최종 손 위치 = 투영 지점 + 벽면 상향 * 도달 거리
                        Vector3 handPoint = projectedShoulderOnWall + wallUp * reachUpDistance;
                        upGoingDistance = reachUpDistance / 2;

                        Debug.DrawLine(arm.position, handPoint, Color.blue);
                        if (_isRightTurn) _rightHandPos = handPoint;
                        else _leftHandPos = handPoint;
                    }
                }

                Debug.DrawRay(calfRay.origin, calfRay.direction * rayMaxDistance, Color.red);
                if (Physics.Raycast(calfRay, out hit, rayMaxDistance, wallLayerMask))
                {
                    if (_isRightTurn) _rightFootPos = hit.point;
                    else _leftFootPos = hit.point;
                }

                // 위치 이동 중에도 벽의 기울기 변화에 맞춰 실시간으로 법선/거리 추적
                float duration = Mathf.Max(wallClimbDuration, 0.01f);
                float counter = 0f;
                while (counter < duration && _stateMachine == StateMachine.Wall)
                {
                    float dt = Time.deltaTime;
                    counter += dt;

                    // 캐릭터 위치 이동
                    transform.position += wallUp * (upGoingDistance * (dt / duration));

                    // 이동 중 벽면과의 거리 및 법선 실시간 보정
                    if (Physics.Raycast(transform.position, transform.forward, out RaycastHit moveHit, rayMaxDistance, wallLayerMask))
                    {
                        _surfaceNormal = moveHit.normal;
                        _surfaceHitPoint = moveHit.point;
                        // 벽과의 간격(wallDistance) 유지
                        transform.position = moveHit.point + moveHit.normal * wallDistance;
                    }

                    yield return null;
                }

                _isRightTurn = !_isRightTurn;
            }
        }
        finally
        {
            _wallClimbCoroutine = null;
        }
    }
    

    private void OnAnimatorIK(int layerIndex)
    {
        if (animator == null) return;

        // 벽 등반 상태일 때만 IK 적용 (필요에 따라 조건 수정)
        float currentHandWeight = (_stateMachine == StateMachine.Wall) ? handIKWeight : 0f;
        float currentFootWeight = (_stateMachine == StateMachine.Wall) ? footIKWeight : 0f;

        // --- 오른손 (Right Hand) ---
        animator.SetIKPositionWeight(AvatarIKGoal.RightHand, currentHandWeight);
        animator.SetIKPosition(AvatarIKGoal.RightHand, _rightHandPos);

        // --- 왼손 (Left Hand) ---
        animator.SetIKPositionWeight(AvatarIKGoal.LeftHand, currentHandWeight);
        animator.SetIKPosition(AvatarIKGoal.LeftHand, _leftHandPos);

        // --- 오른발 (Right Foot) ---
        animator.SetIKPositionWeight(AvatarIKGoal.RightFoot, currentFootWeight);
        animator.SetIKPosition(AvatarIKGoal.RightFoot, _rightFootPos);

        // --- 왼발 (Left Foot) ---
        animator.SetIKPositionWeight(AvatarIKGoal.LeftFoot, currentFootWeight);
        animator.SetIKPosition(AvatarIKGoal.LeftFoot, _leftFootPos);
    }


    public void OnLook(InputAction.CallbackContext context)
    {
        Vector2 mouseDelta = context.ReadValue<Vector2>() * mouseSensitivity;
        float mouseX = mouseDelta.x;
        float mouseY = mouseDelta.y;

        // 1. Pitch 누적 및 클램핑 (공통)
        _pitch = Mathf.Clamp(_pitch - mouseY, minPitch, maxPitch);

        // 2. 상태별 Yaw 처리
        if (_stateMachine == StateMachine.Floor)
        {
            // 지상: 월드 360도 자유 회전
            _yaw += mouseX;
        }
        else
        {
            // 벽 등반: 벽면 기준 둘러보기 범위 제한
            _yaw = Mathf.Clamp(_yaw + mouseX, -wallYawLimit, wallYawLimit);
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

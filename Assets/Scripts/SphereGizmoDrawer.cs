using UnityEngine;

public class SphereGizmoDrawer : MonoBehaviour
{
    [Header("Gizmo Settings")]
    public float radius = 1.0f;
    public Color gizmoColor = Color.red;
    public bool drawWireframe = false;

    // 씬 뷰에서 항상 표시
    private void OnDrawGizmos()
    {
        Gizmos.color = gizmoColor;

        if (drawWireframe)
        {
            // 와이어프레임(외곽선) 구체
            Gizmos.DrawWireSphere(transform.position, radius);
        }
        else
        {
            // 속이 찬 솔리드 구체
            Gizmos.DrawSphere(transform.position, radius);
        }
    }

    // 오브젝트를 선택했을 때만 표시하고 싶다면 OnDrawGizmos 대신 아래 함수를 사용합니다.
    /*
    private void OnDrawGizmosSelected()
    {
        Gizmos.color = Color.yellow;
        Gizmos.DrawWireSphere(transform.position, radius);
    }
    */
}   
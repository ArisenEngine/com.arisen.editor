namespace ArisenEditorFramework.Extensions;

internal sealed class EditorExtensionRegistryCore<TExtension>
    where TExtension : class
{
    private readonly record struct Registration(TExtension Extension, string ExtensionId, int Order);

    private sealed class RegistrationComparer : IComparer<Registration>
    {
        public static RegistrationComparer Instance { get; } = new();

        public int Compare(Registration left, Registration right)
        {
            int order = left.Order.CompareTo(right.Order);
            return order != 0
                ? order
                : StringComparer.Ordinal.Compare(left.ExtensionId, right.ExtensionId);
        }
    }

    private readonly object m_Gate = new();
    private readonly Dictionary<string, Registration> m_Registrations = new(StringComparer.Ordinal);
    private TExtension[] m_ActiveExtensions = Array.Empty<TExtension>();
    private bool m_IsEditorActive;

    public int Count
    {
        get
        {
            lock (m_Gate)
            {
                return m_Registrations.Count;
            }
        }
    }

    public bool IsEditorActive
    {
        get
        {
            lock (m_Gate)
            {
                return m_IsEditorActive;
            }
        }
    }

    public void Register(TExtension extension, string extensionId, int order)
    {
        ArgumentNullException.ThrowIfNull(extension);
        ValidateExtensionId(extensionId);

        lock (m_Gate)
        {
            if (m_IsEditorActive)
            {
                throw new InvalidOperationException(
                    $"[Editor.Extensions] Cannot register extension '{extensionId}' after Editor activation. " +
                    "Register extensions during package OnLoad before the Editor host starts.");
            }

            if (!m_Registrations.TryAdd(
                    extensionId,
                    new Registration(extension, extensionId, order)))
            {
                throw new InvalidOperationException(
                    $"[Editor.Extensions] Extension ID '{extensionId}' is already registered.");
            }
        }
    }

    public bool Unregister(TExtension extension, string extensionId)
    {
        ArgumentNullException.ThrowIfNull(extension);
        ValidateExtensionId(extensionId);

        lock (m_Gate)
        {
            if (m_IsEditorActive)
            {
                throw new InvalidOperationException(
                    $"[Editor.Extensions] Cannot unregister extension '{extensionId}' while the Editor is active.");
            }

            if (!m_Registrations.TryGetValue(extensionId, out var registration))
            {
                return false;
            }

            if (!ReferenceEquals(registration.Extension, extension))
            {
                throw new InvalidOperationException(
                    $"[Editor.Extensions] Extension ID '{extensionId}' is registered to another instance.");
            }

            return m_Registrations.Remove(extensionId);
        }
    }

    public TExtension[] BeginEditorActivation()
    {
        lock (m_Gate)
        {
            if (m_IsEditorActive)
            {
                throw new InvalidOperationException(
                    "[Editor.Extensions] The extension registry is already frozen for an active Editor host.");
            }

            if (m_Registrations.Count == 0)
            {
                m_ActiveExtensions = Array.Empty<TExtension>();
            }
            else
            {
                var registrations = new Registration[m_Registrations.Count];
                m_Registrations.Values.CopyTo(registrations, 0);
                Array.Sort(registrations, RegistrationComparer.Instance);

                var activeExtensions = new TExtension[registrations.Length];
                for (int i = 0; i < registrations.Length; i++)
                {
                    activeExtensions[i] = registrations[i].Extension;
                }

                m_ActiveExtensions = activeExtensions;
            }

            m_IsEditorActive = true;
            return m_ActiveExtensions;
        }
    }

    public void EndEditorActivation()
    {
        lock (m_Gate)
        {
            m_IsEditorActive = false;
            m_ActiveExtensions = Array.Empty<TExtension>();
        }
    }

    private static void ValidateExtensionId(string extensionId)
    {
        if (string.IsNullOrWhiteSpace(extensionId) ||
            !string.Equals(extensionId, extensionId.Trim(), StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "[Editor.Extensions] Extension ID must be non-empty and cannot have leading or trailing whitespace.",
                nameof(extensionId));
        }
    }
}

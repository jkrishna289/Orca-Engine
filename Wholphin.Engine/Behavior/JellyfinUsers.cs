using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace Wholphin.Engine.Behavior;

/// <summary>
/// Resolves the ids of every Jellyfin account, across host versions that disagree about how to ask.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why reflection, in a codebase that otherwise avoids it.</b> The way to enumerate users changed
/// <em>within</em> the 10.11 patch series and the two shapes are mutually exclusive:
/// </para>
/// <list type="bullet">
/// <item><description>10.11.0 has an <c>IEnumerable&lt;User&gt; Users</c> property and no <c>GetUsers()</c>.</description></item>
/// <item><description>10.11.11 has <c>GetUsers()</c> and <c>GetUsersIds()</c> and no <c>Users</c> property.</description></item>
/// </list>
/// <para>
/// Binding either at compile time throws <c>MissingMethodException</c> on the other, which is
/// exactly what shipped in 1.2.0.0: built against 10.11.0, run on 10.11.11, and the watch-history
/// import died on its first line. The plugin targets ABI 10.11.0.0 and is installed on whatever
/// patch an operator happens to run, so it has to ask in a way that works either way.
/// </para>
/// <para>
/// Deliberately scoped to <b>ids only</b>. Everything else stays strongly typed:
/// <c>IUserManager.GetUserById</c> is stable across both versions and returns the same
/// <c>User</c> type, so callers get a real object back for the user-data calls.
/// </para>
/// </remarks>
public static class JellyfinUsers
{
    /// <summary>
    /// Returns every account id, or an empty list when no known shape is present.
    /// </summary>
    /// <param name="userManager">The Jellyfin user manager.</param>
    /// <returns>The account ids, in the host's order, without duplicates.</returns>
    /// <remarks>
    /// Takes <see cref="object"/> rather than <c>IUserManager</c> so the resolution can be tested
    /// against each host shape without standing up the whole interface.
    /// </remarks>
    public static IReadOnlyList<Guid> AllIds(object userManager)
    {
        if (userManager is null)
        {
            return Array.Empty<Guid>();
        }

        var type = userManager.GetType();

        // Newest first: this one hands back ids directly and skips materializing user objects.
        if (Invoke(type, userManager, "GetUsersIds") is IEnumerable ids)
        {
            var direct = ids.OfType<Guid>().Distinct().ToList();
            if (direct.Count > 0)
            {
                return direct;
            }
        }

        var users = Invoke(type, userManager, "GetUsers")
                    ?? type.GetProperty("Users", BindingFlags.Public | BindingFlags.Instance)?.GetValue(userManager);

        if (users is not IEnumerable sequence)
        {
            return Array.Empty<Guid>();
        }

        var found = new List<Guid>();
        foreach (var user in sequence)
        {
            if (user is null)
            {
                continue;
            }

            if (user.GetType().GetProperty("Id", BindingFlags.Public | BindingFlags.Instance)?.GetValue(user) is Guid id
                && id != Guid.Empty)
            {
                found.Add(id);
            }
        }

        return found.Distinct().ToList();
    }

    /// <summary>Calls a public parameterless method by name, or returns null when it does not exist.</summary>
    private static object? Invoke(Type type, object instance, string name)
    {
        var method = type.GetMethod(name, BindingFlags.Public | BindingFlags.Instance, Type.EmptyTypes);
        return method?.Invoke(instance, null);
    }
}

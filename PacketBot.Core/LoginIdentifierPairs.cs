using System.Collections.Generic;

namespace FunnyProject
{
    // Configure the HTTP form field names and keep each pair together.
    // Replace the example values with identifiers recognized by your server.
    internal static class LoginIdentifierPairs
    {
        public const string FirstParameter = "captcha_id";
        public const string SecondParameter = "captcha_answer";

        internal sealed class Pair
        {
            public string First { get; }
            public string Second { get; }

            public Pair(string first, string second)
            {
                First = first;
                Second = second;
            }

            public override string ToString() => Second;
        }

        public static readonly IReadOnlyList<Pair> All = new[]
        {
            new Pair("28937bf36a418d99fa3fb1956677047951c054fd", "cc2usd"),
            new Pair("d0702e8b2286afac53aea5bbbc54f5374b58ce57", "uhapd2"),
            new Pair("59187731b4021b2d6d3e03cc5172e7b843dc5b8d", "nprxdm"),
            new Pair("e9543ac8bb53a2f2c3866d46d73e04a354a8f85e", "fm2ggn"),
            new Pair("4c340beb59aac1431c7ce792469eeca01330be60", "24pew"),
            new Pair("74a5398f136208839f779b65b8eb16f3fc48f7a8", "rgbrmp"),
            new Pair("af8eebd2ea639434d7662ac9956a9f2b5cf0baa0", "28348"),
            new Pair("ab6432a4968a10e3d04f8c3e5327734a64bb773d", "pbbgcz"),
            new Pair("7a50d0a1f3e50030399b894468541807c60e0b94", "ae6xah"),
            new Pair("abf210562119e205f11219854a19134060125995", "nsrarf"),
            new Pair("fc25eb16467d690cf5af2dbb63f91ee33babaf9e", "6edzma"),
            new Pair("122624bf9a416ca9e5129e4617d4b23f25f12272", "23n88"),
            new Pair("0a74963416b1b06d0b1548cd54e38391fd575cf9", "2b827"),
            new Pair("41eb3be649d89471eecc3126e1f12d8eac0228f7", "2cg58"),
            new Pair("d5b9147fe3d7dd3c135d2f8f1e486b30d7b7515b", "24f6w"),
            new Pair("3184e6149873fb0f311fe557d1440935f61b7622", "nggtt2"),
        };
    }
}

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CardiTrack.Infrastructure.Migrations
{
    /// <summary>
    /// Retires <c>Gender.Other</c>, moving the members who held it to <c>PreferNotToSay</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Data only — there is no schema change to make. <c>Gender</c> is persisted through
    /// <c>HasConversion&lt;string&gt;()</c>, so the column is a plain <c>character varying(50)</c>
    /// that neither knows nor constrains which enum names are legal; dropping a member from the
    /// enum leaves the rows holding it perfectly valid to Postgres and unmappable to C#. Left
    /// alone, the first read of such a row would throw inside the value converter, on a query
    /// nobody had changed.
    /// </para>
    /// <para>
    /// <c>PreferNotToSay</c> is the honest destination. Both values mean the same thing to every
    /// clinical path — no usable sex was recorded — and the prompt layer renders that as
    /// "not stated". Guessing a sex here to make the data look complete would put a fabricated
    /// reference range behind a health summary, which is the one outcome worth avoiding.
    /// </para>
    /// </remarks>
    public partial class RetireOtherGender : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                UPDATE "CardiMembers" SET "Gender" = 'PreferNotToSay' WHERE "Gender" = 'Other';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Deliberately empty. Which rows said "Other" is not recoverable once they say
            // "PreferNotToSay", and there is no column here to restore. Throwing instead would
            // make this migration a wall that any later schema rollback could not get past, to
            // protect a value the enum no longer defines.
        }
    }
}

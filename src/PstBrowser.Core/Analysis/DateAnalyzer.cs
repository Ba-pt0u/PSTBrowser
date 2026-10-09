using System;
using System.Collections.Generic;
using System.Linq;

namespace PstBrowser.Core.Analysis
{
    [Flags]
    public enum DateFlags
    {
        None = 0,
        DeliveredBeforeSent = 1,
        ModifiedBeforeCreated = 2,
        HeaderDateSkew = 4,
        ReceivedChainBackwards = 8,
        FutureDate = 16,
        ImplausiblyOld = 32,
        ModifiedBeforeDelivered = 64,
    }

    public sealed class DateEntry
    {
        public string Label { get; set; }
        public DateTime? Value { get; set; }
        public string Source { get; set; }
    }

    public sealed class DateAnalysis
    {
        public List<DateEntry> Dates { get; } = new List<DateEntry>();
        public DateFlags Flags { get; set; }
        public List<Finding> Findings { get; } = new List<Finding>();
    }

    /// <summary>
    /// Consistency of the dates of a message: delivery, sending, creation, modification, "Date:" header and the time stamps of the servers.
    /// A finding is an indication to look into (clock skew, import or migration tools, manual editing), not a proof of tampering.
    /// </summary>
    public static class DateAnalyzer
    {
        public static readonly TimeSpan Tolerance = TimeSpan.FromMinutes(5);
        public static readonly TimeSpan HeaderTolerance = TimeSpan.FromHours(2);
        private static readonly DateTime EmailEra = new DateTime(1995, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        /// <param name="hops">Servers the message went through, oldest first (may be empty). All dates are UTC.</param>
        public static DateAnalysis Analyze(DateTime? delivery, DateTime? sent, DateTime? created, DateTime? modified, DateTime? headerDate,
                                           IEnumerable<ReceivedHop> hops, DateTime now)
        {
            var a = new DateAnalysis();
            a.Dates.Add(new DateEntry { Label = "Envoi (heure de l'expéditeur)", Value = sent, Source = "propriété du message" });
            a.Dates.Add(new DateEntry { Label = "Date de l'en-tête « Date: »", Value = headerDate, Source = "en-têtes Internet" });
            var hopList = hops?.Where(h => h.Time.HasValue).ToList() ?? new List<ReceivedHop>();
            if (hopList.Count > 0)
            {
                a.Dates.Add(new DateEntry { Label = "Premier serveur (Received)", Value = hopList.First().Time, Source = hopList.First().By });
                a.Dates.Add(new DateEntry { Label = "Dernier serveur (Received)", Value = hopList.Last().Time, Source = hopList.Last().By });
            }
            a.Dates.Add(new DateEntry { Label = "Réception (livraison)", Value = delivery, Source = "propriété du message" });
            a.Dates.Add(new DateEntry { Label = "Création de l'élément", Value = created, Source = "propriété du message" });
            a.Dates.Add(new DateEntry { Label = "Dernière modification", Value = modified, Source = "propriété du message" });

            void Add(DateFlags f, string code, string text) { a.Flags |= f; a.Findings.Add(new Finding { Code = code, Text = text }); }
            string F(DateTime d) => d.ToLocalTime().ToString("dd/MM/yyyy HH:mm:ss");

            if (delivery.HasValue && sent.HasValue && delivery.Value < sent.Value - Tolerance)
                Add(DateFlags.DeliveredBeforeSent, "delivered-before-sent", $"Le message est reçu ({F(delivery.Value)}) avant d'avoir été envoyé ({F(sent.Value)}) : décalage d'horloge de l'expéditeur, ou date modifiée.");
            if (modified.HasValue && created.HasValue && modified.Value < created.Value - Tolerance)
                Add(DateFlags.ModifiedBeforeCreated, "modified-before-created", $"L'élément est modifié ({F(modified.Value)}) avant d'avoir été créé ({F(created.Value)}).");
            if (modified.HasValue && delivery.HasValue && modified.Value < delivery.Value - Tolerance)
                Add(DateFlags.ModifiedBeforeDelivered, "modified-before-delivered", $"La dernière modification ({F(modified.Value)}) est antérieure à la réception ({F(delivery.Value)}) : impossible pour un élément reçu normalement (outil d'import ou de migration, ou modification manuelle).");
            if (headerDate.HasValue && sent.HasValue && (headerDate.Value - sent.Value).Duration() > HeaderTolerance)
                Add(DateFlags.HeaderDateSkew, "header-date-skew", $"La date de l'en-tête « Date: » ({F(headerDate.Value)}) diffère de plus de {HeaderTolerance.TotalHours:0} h de la date d'envoi ({F(sent.Value)}).");
            for (int i = 1; i < hopList.Count; i++)
                if (hopList[i].Time.Value < hopList[i - 1].Time.Value - Tolerance)
                {
                    Add(DateFlags.ReceivedChainBackwards, "received-backwards", $"Dans le chemin des serveurs, le serveur n°{hopList[i].Index} ({F(hopList[i].Time.Value)}) est daté avant le serveur précédent ({F(hopList[i - 1].Time.Value)}) : horloges désynchronisées ou en-têtes ajoutés.");
                    break;
                }
            var all = a.Dates.Where(d => d.Value.HasValue && d.Value.Value.Year > 1700).Select(d => d).ToList();
            foreach (var d in all)
            {
                if (d.Value.Value > now + TimeSpan.FromDays(1)) { Add(DateFlags.FutureDate, "future", $"« {d.Label} » est dans le futur ({F(d.Value.Value)})."); break; }
            }
            foreach (var d in all)
            {
                if (d.Value.Value < EmailEra) { Add(DateFlags.ImplausiblyOld, "too-old", $"« {d.Label} » est antérieure à 1995 ({F(d.Value.Value)}), avant l'usage courant du courrier électronique."); break; }
            }
            return a;
        }
    }
}

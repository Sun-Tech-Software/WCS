namespace Wcs.Engine.Items
{
    /// <summary>
    /// Rappresenta una giunzione di automazione che collega due dispositivi di automazione,
    /// definendo una relazione di collegamento tra un dispositivo sorgente e uno di destinazione
    /// con una priorità associata per la gestione del flusso di unità di carico.
    /// </summary>
    public class AutomationJunction
    {

        #region Public Constructors
                
        /// <summary>
        /// Inizializza una nuova istanza della classe <see cref="AutomationJunction"/> 
        /// con un nuovo identificatore univoco generato automaticamente.
        /// </summary>
        public AutomationJunction()
        {
            ItemGuid = Guid.NewGuid();
            Initialize();
        }

        /// <summary>
        /// Inizializza una nuova istanza della classe <see cref="AutomationJunction"/>
        /// con gli identificatori dei dispositivi sorgente e destinazione specificati.
        /// </summary>
        /// <param name="sourceItemId">L'identificatore univoco del dispositivo sorgente</param>
        /// <param name="destinationItemId">L'identificatore univoco del dispositivo di destinazione</param>
        public AutomationJunction(Guid sourceItemId, Guid destinationItemId)
        {
            SourceId = sourceItemId;
            DestinationId = destinationItemId;
            Initialize();
        }

        /// <summary>Crea una giunzione da una riga gia' catturata, senza interrogare o scrivere lo store.</summary>
        internal AutomationJunction(Guid sourceItemId, Guid destinationItemId, bool hydrateOnly)
        {
            SourceId = sourceItemId;
            DestinationId = destinationItemId;
        }

        #endregion Public Constructors

        #region Public Properties

        /// <summary>
        /// Ottiene o imposta l'identificatore univoco del dispositivo di destinazione della giunzione.
        /// </summary>
        /// <value>L'identificatore univoco del dispositivo di destinazione</value>
        public Guid DestinationId { get; set; }

        /// <summary>
        /// Ottiene o imposta il dispositivo di automazione di destinazione associato a questa giunzione.
        /// Quando viene impostato, aggiorna automaticamente il <see cref="DestinationId"/> corrispondente.
        /// </summary>
        /// <value>Il dispositivo di automazione di destinazione, oppure null se non trovato</value>
        public AutomationDevice? DestinationItem
        {
            get => Collections.PathDevices.Instance[DestinationId];
            set => DestinationId = value?.ItemGuid ?? Guid.Empty;
        }

        /// <summary>
        /// Ottiene o imposta l'identificatore univoco di questa giunzione di automazione.
        /// </summary>
        /// <value>L'identificatore univoco della giunzione</value>
        public Guid ItemGuid { get; set; }

        /// <summary>
        /// Ottiene o imposta la priorità di questa giunzione per la gestione del flusso di unità di carico.
        /// Valori più alti indicano priorità maggiore.
        /// </summary>
        /// <value>Il valore di priorità della giunzione</value>
        public int Priority { get; set; }

        /// <summary>
        /// Ottiene o imposta l'identificatore univoco del dispositivo sorgente della giunzione.
        /// </summary>
        /// <value>L'identificatore univoco del dispositivo sorgente</value>
        public Guid SourceId { get; set; }

        /// <summary>
        /// Ottiene o imposta il dispositivo di automazione sorgente associato a questa giunzione.
        /// Quando viene impostato, aggiorna automaticamente il <see cref="SourceId"/> corrispondente.
        /// </summary>
        /// <value>Il dispositivo di automazione sorgente, oppure null se non trovato</value>
        public AutomationDevice? SourceItem
        {
            get => Collections.PathDevices.Instance[SourceId];
            set => SourceId = value?.ItemGuid ?? Guid.Empty;
        }

        #endregion Public Properties

        #region Private Methods

        /// <summary>
        /// Inizializza la giunzione di automazione verificando se esiste già nel database ORM
        /// e creando o aggiornando il record appropriato.
        /// </summary>
        private void Initialize()
        {
            if (ORM.AutomationJunction.Get(this)) return;
            if (ItemGuid == Guid.Empty) ItemGuid = Guid.NewGuid();
            ORM.AutomationJunction.Update(this);
        }

        #endregion Private Methods
    }
}

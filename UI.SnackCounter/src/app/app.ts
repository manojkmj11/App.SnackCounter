import { CommonModule, DatePipe } from '@angular/common';
import { Component, HostListener, OnDestroy, OnInit, computed, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { HubConnection, HubConnectionBuilder, LogLevel } from '@microsoft/signalr';
import { firstValueFrom } from 'rxjs';
import { HttpClient, HttpErrorResponse } from '@angular/common/http';

const API = '/api';
const HUB = '/hubs/notifications';
type Office = 'Oslo' | 'Bengaluru';
type Currency = 'NOK' | 'INR';
type User = { employeeId: string; displayName: string; email: string; homeOffice: Office; payrollCurrency: Currency; roles: string[] };
type Snack = { id: string; office: Office; name: string; unit: string; quantityOnHand: number; reorderThreshold: number; isActive: boolean; isClaimed: boolean; rowVersion: string };
type Refill = { id: string; office: Office; item: string; status: string; assignedEmployeeId?: string; createdAt: string; assignmentExpiresAt?: string; purchaseAmount?: number; purchaseCurrency?: Currency; version: number; requesterEmployeeId?: string; snackItemId?: string; rowVersion: string };
type RefillOfferState = 'None' | 'Offered' | 'Acknowledged' | 'Accepted';
type OfficeRefill = { refill: Refill; itemRowVersion?: string; suggestedEmployeeId?: string | null; suggestedEmployeeName?: string | null; suggestionReason?: string; canAcceptOffer?: boolean; offerExpiresAt?: string | null; offerState?: RefillOfferState };
type Claim = { id: string; employeeId: string; office: Office; purchaseId: string; refillId?: string; submissionAttempt: number; previousClaimId?: string; amount: number; currency: Currency; employeePayrollCurrency?: Currency; description: string; status: string; processingByEmployeeId?: string; decisionReason?: string; externalHrClaimId?: string; submittedAt: string; hasReceipt: boolean; rowVersion: string };
type Purchase = { purchaseId: string; office: Office; item: string; amount: number; currency: Currency; purchasedAt: string; description: string; claimId?: string; claimStatus?: string; submissionAttempt?: number; decisionReason?: string };
type ClaimHistoryEntry = { purchaseKey: string; claim: Claim; attempts: number; wasDenied: boolean };
type Leave = { id: string; office: Office; startDate: string; endDate: string; status: string };
type OfficeVisit = { id: string; office: Office; startDate: string; endDate: string; cancelledAt?: string };
type Conversation = { id: string; kind: string; office?: Office; title: string; latestMessage?: Message; unreadCount: number };
type Message = { id: string; conversationId: string; senderEmployeeId?: string; kind: string; text?: string; createdAt: string };
type Notification = { id: string; eventType: string; recipientEmployeeId?: string; office?: Office; isHrAudience?: boolean; conversationMessageId?: string; payloadJson?: string; createdAt: string; readAt?: string };
type OfficeReport = { office: Office; currency: Currency; budget: number; spent: number; remaining: number; mostConsumedItems: { item: string; quantity: number }[] };
type PantryOverview = { office: Office; month: string; currency: Currency; budget: number; spent: number; remaining: number; budgetReached: boolean; topRefilledItems: { item: string; refillCount: number; quantityAdded: number }[] };
type ReportSnapshot = { offices: OfficeReport[]; totalsByCurrency: Partial<Record<Currency, number>>; asOf: string };
type OfficeSettings = { office: Office; timeZoneId: string; budgetCurrency: Currency };
type VisitOfficeOption = { office: Office; name: string; timeZoneId: string };
type DemoIdentity = Pick<User, 'employeeId' | 'displayName' | 'email' | 'homeOffice' | 'payrollCurrency' | 'roles'>;

@Component({
  imports: [CommonModule, FormsModule, DatePipe],
  selector: 'app-root',
  styleUrl: './app.css',
  templateUrl: './app.html',
})
export class App implements OnInit, OnDestroy {
  readonly identities = signal<DemoIdentity[]>([]);
  readonly user = signal<User | null>(null);
  readonly page = signal('overview');
  readonly busy = signal(false);
  readonly loading = signal(true);
  readonly error = signal('');
  readonly notice = signal('');
  readonly mobileNavOpen = signal(false);
  readonly inventory = signal<Snack[]>([]);
  readonly refills = signal<Refill[]>([]);
  readonly refillQueue = signal<OfficeRefill[]>([]);
  readonly reportEmptyOpen = signal(false);
  readonly newItemFormOpen = signal(false);
  readonly claims = signal<Claim[]>([]);
  readonly pendingExternalClaimDecisions = signal<ReadonlySet<string>>(new Set());
  payrollRates: Record<string, number | null> = {};
  payrollRateSources: Record<string, string> = {};
  readonly employeeClaims = signal<Claim[]>([]);
  readonly purchases = signal<Purchase[]>([]);
  readonly reimbursementReceipt = signal<File | null>(null);
  readonly leaves = signal<Leave[]>([]);
  readonly officeVisits = signal<OfficeVisit[]>([]);
  readonly visitOptions = signal<VisitOfficeOption[]>([]);
  readonly visitDestinations = computed(() => this.visitOptions().filter(option => option.office !== this.user()?.homeOffice));
  readonly offices = signal<Office[]>(['Oslo', 'Bengaluru']);
  readonly officeCurrencies = signal<Record<Office, Currency>>({ Oslo: 'NOK', Bengaluru: 'INR' });
  readonly officeTimeZones = signal<Record<Office, string>>({ Oslo: 'Europe/Oslo', Bengaluru: 'Asia/Kolkata' });
  readonly reports = signal<OfficeReport[]>([]);
  readonly monthlyOverview = signal<PantryOverview | null>(null);
  readonly reportTotals = signal<Partial<Record<Currency, number>>>({});
  readonly reportCurrencies: Currency[] = ['NOK', 'INR'];
  readonly conversations = signal<Conversation[]>([]);
  readonly messages = signal<Message[]>([]);
  readonly notifications = signal<Notification[]>([]);
  readonly activeConversation = signal<Conversation | null>(null);
  emptyReportItemId = '';
  emptyItemSuggestionsDismissed = false;
  readonly selectedOffice = signal<Office>('Oslo');
  workingMode: 'WFO' | 'WFH' = 'WFO';
  workingOffice: Office = 'Oslo';
  readonly isHr = computed(() => this.user()?.roles.includes('HR') ?? false);
  readonly isManager = computed(() => this.user()?.roles.includes('Manager') ?? false);
  readonly assignedToMe = computed(() => this.refillQueue()
    .filter(entry => entry.refill.status === 'Assigned' && this.isAssignedToMe(entry.refill) &&
      (entry.offerState !== 'Offered' || entry.canAcceptOffer))
    .map(entry => entry.refill));
  readonly needsStock = computed(() => this.inventory().filter(x => x.isActive && x.quantityOnHand <= 0));
  readonly openRefillQueue = computed(() => this.refillQueue().filter(x => x.refill.status === 'Open'));
  readonly claimedRefillQueue = computed(() => this.refillQueue().filter(x => x.refill.status === 'Assigned'));
  readonly unreadCount = computed(() => this.notifications().filter(x => !x.readAt).length);
  readonly myClaims = computed(() => this.employeeClaims().filter(x => x.employeeId === this.user()?.employeeId));
  readonly claimHistory = computed<ClaimHistoryEntry[]>(() => {
    const purchases = this.purchases();
    const groups = new Map<string, Claim[]>();
    for (const claim of this.myClaims()) {
      const purchase = purchases.find(item => item.purchaseId === claim.purchaseId || item.purchaseId === claim.refillId);
      const key = purchase?.purchaseId ?? claim.refillId ?? claim.purchaseId;
      const rows = groups.get(key) ?? [];
      rows.push(claim); groups.set(key, rows);
    }
    return [...groups].map(([purchaseKey, rows]) => {
      rows.sort((a, b) => new Date(b.submittedAt).getTime() - new Date(a.submittedAt).getTime());
      return { purchaseKey, claim: rows[0], attempts: rows.length, wasDenied: rows.some(row => row.status === 'Denied') };
    }).sort((a, b) => new Date(b.claim.submittedAt).getTime() - new Date(a.claim.submittedAt).getTime());
  });
  readonly activeClaimCount = computed(() => this.claimHistory().filter(x => ['Submitted', 'Processing', 'Approved'].includes(x.claim.status)).length);
  readonly selectedPurchase = computed(() => this.purchases().find(x => x.purchaseId === this.claimPurchaseId()));
  readonly selectedCurrency = computed<Currency>(() => this.officeCurrencies()[this.selectedOffice()]);
  readonly todayDate = computed(() => {
    const parts = new Intl.DateTimeFormat(undefined, {
      timeZone: this.officeTimeZones()[this.selectedOffice()], day: '2-digit', month: 'long',
    }).formatToParts(new Date());
    return { day: parts.find(part => part.type === 'day')?.value ?? '', month: parts.find(part => part.type === 'month')?.value ?? '' };
  });
  identityEmail = localStorage.getItem('snackcounter.email') ?? '';
  refillQuantity = 1;
  refillAmount: number | null = null;
  refillDescription = '';
  readonly claimPurchaseId = signal('');
  claimDescription = '';
  leaveStart = '';
  leaveEnd = '';
  visitOffice: Office = 'Oslo';
  visitStartDate = '';
  visitEndDate = '';
  chatText = '';
  newItemName = '';
  newPantryItemName = '';
  newItemUnit = 'pack';
  newItemThreshold = 3;
  month = `${new Date().getFullYear()}-${String(new Date().getMonth() + 1).padStart(2, '0')}`;
  private hub?: HubConnection;
  private officeAccessTimer?: number;
  private officeAccessRefreshRunning = false;
  private refreshGeneration = 0;
  private messageLoadGeneration = 0;

  constructor(private readonly http: HttpClient) {}

  async ngOnInit(): Promise<void> {
    const saved = localStorage.getItem('snackcounter.email');
    try {
      const identities = await firstValueFrom(this.http.get<DemoIdentity[]>(`${API}/auth/demo-identities`));
      this.identities.set(identities);
      if (saved && identities.some(identity => identity.email === saved)) {
        this.identityEmail = saved;
        await this.login();
        return;
      }
      if (saved) localStorage.removeItem('snackcounter.email');
      this.identityEmail = identities[0]?.email ?? '';
    } catch (error) {
      this.setError(error, 'Demo accounts could not be loaded. Check that the API is running.');
    } finally {
      this.loading.set(false);
    }
  }

  ngOnDestroy(): void {
    if (this.officeAccessTimer) window.clearInterval(this.officeAccessTimer);
    void this.hub?.stop();
  }

  selectLoginIdentity(email: string): void {
    this.identityEmail = email;
  }

  async login(): Promise<void> {
    this.busy.set(true); this.error.set('');
    try {
      const response = await firstValueFrom(this.http.post<{ user: User; currentOffice: Office }>(`${API}/auth/dev-login`, { email: this.identityEmail }));
      if (this.user()?.employeeId !== response.user.employeeId) this.clearAccountState();
      localStorage.setItem('snackcounter.email', this.identityEmail);
      localStorage.removeItem('snackcounter.office');
      this.user.set(response.user);
      this.selectedOffice.set(response.currentOffice);
      this.workingOffice = response.currentOffice;
      this.page.set('overview');
      await this.refreshAll();
      await this.connectHub();
      this.startOfficeAccessRefresh();
    } catch (error) { this.setError(error, 'Could not sign in. Please try again.'); }
    finally { this.loading.set(false); this.busy.set(false); }
  }

  async refreshAll(): Promise<void> {
    const u = this.user(); if (!u) return;
    this.loading.set(true); this.error.set('');
    const refreshId = ++this.refreshGeneration;
    const isUserCurrent = () => refreshId === this.refreshGeneration && this.user()?.employeeId === u.employeeId;
    try {
      const visits = await this.get<OfficeVisit[]>('me/office-visits');
      if (!isUserCurrent()) return;
      this.officeVisits.set(visits);
      this.setCurrentOffice(this.officeForToday(visits, u));
    } catch (error) {
      if (!isUserCurrent()) return;
      this.setError(error, 'Your office visit could not be checked.');
    }
    const office = this.selectedOffice();
    const isCurrent = () => refreshId === this.refreshGeneration &&
      this.selectedOffice() === office && this.user()?.employeeId === u.employeeId;
    const tasks: Promise<void>[] = [
      this.get<Snack[]>(`offices/${office}/inventory`).then(v => { if (isCurrent()) this.inventory.set(v); }),
      this.get<OfficeRefill[]>(`offices/${office}/refill-requests`).then(v => { if (isCurrent()) this.refillQueue.set(v); }),
      this.get<PantryOverview>(`offices/${office}/monthly-overview`).then(v => { if (isCurrent()) this.monthlyOverview.set(v); }),
      this.get<Refill[]>(`me/refills?office=${encodeURIComponent(office)}`).then(v => { if (isCurrent()) this.refills.set(v); }),
      this.get<Claim[]>('me/claims').then(v => { if (isCurrent()) this.employeeClaims.set(v); }),
      this.get<Purchase[]>('me/purchases').then(v => { if (isCurrent()) this.purchases.set(v); }),
      this.get<Leave[]>(`me/leaves?office=${encodeURIComponent(office)}`).then(v => { if (isCurrent()) this.leaves.set(v); }),
      this.get<VisitOfficeOption[]>('offices/visit-options').then(v => {
        if (!isCurrent()) return;
        this.visitOptions.set(v);
        this.officeTimeZones.update(current => ({ ...current, ...Object.fromEntries(v.map(option => [option.office, option.timeZoneId])) }));
        if (!this.visitDestinations().some(option => option.office === this.visitOffice))
          this.visitOffice = this.visitDestinations()[0]?.office ?? v[0]?.office ?? 'Oslo';
      }),
      this.get<Notification[]>(`me/notifications?office=${encodeURIComponent(office)}&pageSize=50`).then(v => { if (isCurrent()) this.notifications.set(v); }),
      this.get<Conversation[]>(`me/conversations?office=${encodeURIComponent(office)}`).then(v => { if (isCurrent()) this.applyConversations(v); }),
      this.get<OfficeSettings[]>('offices').then(v => {
        if (!isCurrent()) return;
        this.offices.set(v.map(x => x.office));
        this.officeCurrencies.set(Object.fromEntries(v.map(x => [x.office, x.budgetCurrency])) as Record<Office, Currency>);
        this.officeTimeZones.set(Object.fromEntries(v.map(x => [x.office, x.timeZoneId])) as Record<Office, string>);
      }),
    ];
    if (u.roles.includes('HR')) tasks.push(this.get<Claim[]>('hr/claims').then(v => { if (isCurrent()) this.setHrClaims(v); }));
    if (u.roles.some(x => ['HR', 'Manager'].includes(x))) tasks.push(this.get<ReportSnapshot>(`reports/snapshot?month=${this.month}`).then(v => { if (isCurrent()) { this.reports.set(v.offices); this.reportTotals.set(v.totalsByCurrency); } }));
    const results = await Promise.allSettled(tasks);
    if (!isCurrent()) return;
    const failed = results.find(x => x.status === 'rejected') as PromiseRejectedResult | undefined;
    if (failed) this.setError(failed.reason, 'We could not load the pantry just now. Check your connection and try again.');
    this.loading.set(false);
  }

  private setCurrentOffice(office: Office): void {
    if (office === this.selectedOffice()) return;
    this.selectedOffice.set(office);
    this.inventory.set([]); this.refillQueue.set([]); this.refills.set([]); this.claims.set([]); this.employeeClaims.set([]); this.leaves.set([]);
    this.monthlyOverview.set(null);
    this.notifications.set([]); this.conversations.set([]); this.activeConversation.set(null); this.messages.set([]);
    this.messageLoadGeneration++;
    this.workingOffice = office;
  }

  private startOfficeAccessRefresh(): void {
    if (this.officeAccessTimer) window.clearInterval(this.officeAccessTimer);
    this.officeAccessTimer = window.setInterval(async () => {
      const currentUser = this.user();
      if (!currentUser || this.busy() || this.loading() || this.officeAccessRefreshRunning) return;
      this.officeAccessRefreshRunning = true;
      try {
        await this.refreshAll();
        if (this.user()?.employeeId !== currentUser.employeeId) return;
        await this.connectHub();
      } catch { /* A later access refresh retries when the API is available. */ }
      finally { this.officeAccessRefreshRunning = false; }
    }, 60_000);
  }

  async reportItemEmpty(item: Snack): Promise<void> {
    await this.mutate(async () => {
      const result = await this.post<{ confirmation: string }>(
        'me/chat/report-empty',
        { office: this.selectedOffice(), snackItemId: item.id, itemRowVersion: item.rowVersion });
      this.reportEmptyOpen.set(false); this.newItemName = ''; this.flash(result.confirmation); await this.refreshAll();
    });
  }

  async reportEmptyItemById(itemId: string): Promise<void> {
    const item = this.inventory().find(candidate => candidate.id === itemId);
    if (!item) { this.error.set('Choose an item from this pantry first.'); return; }
    await this.reportItemEmpty(item);
  }

  emptyItemSuggestions(): Snack[] {
    const query = this.newItemName.trim().toLocaleLowerCase();
    if (!query || this.emptyItemSuggestionsDismissed) return [];
    return this.inventory()
      .filter(item => item.isActive && item.name.toLocaleLowerCase().includes(query))
      .sort((a, b) => {
        const aStartsWithQuery = a.name.toLocaleLowerCase().startsWith(query);
        const bStartsWithQuery = b.name.toLocaleLowerCase().startsWith(query);
        return Number(bStartsWithQuery) - Number(aStartsWithQuery) || a.name.localeCompare(b.name);
      })
      .slice(0, 8);
  }

  selectEmptyItemSuggestion(name: string): void {
    this.newItemName = name;
    this.emptyItemSuggestionsDismissed = true;
  }

  onEmptyItemNameChanged(): void {
    this.emptyItemSuggestionsDismissed = false;
  }

  async requestRefillByName(): Promise<void> {
    const name = this.newItemName.trim();
    if (!name) { this.error.set('Enter the name of the empty pantry item.'); return; }
    const item = this.inventory().find(x => x.name.toLocaleLowerCase() === name.toLocaleLowerCase() && x.isActive);
    if (!item) { this.error.set('That item is not in this pantry catalog.'); return; }
    await this.reportItemEmpty(item);
    this.newItemName = '';
  }

  async addItem(): Promise<void> {
    if (!this.newPantryItemName.trim()) return;
    const office = this.selectedOffice();
    await this.mutate(async () => {
      await this.post(`offices/${office}/inventory/items`, { name: this.newPantryItemName.trim(), unit: this.newItemUnit, reorderThreshold: Number(this.newItemThreshold) });
      const inventory = await this.get<Snack[]>(`offices/${office}/inventory`);
      const item = inventory.find(candidate => candidate.name.toLocaleLowerCase() === this.newPantryItemName.trim().toLocaleLowerCase());
      if (!item) throw new Error('The item was created but could not be found in this pantry. Refresh and try again.');
      await this.post(`offices/${office}/refill-requests`, { snackItemId: item.id });
      this.newPantryItemName = ''; this.newItemFormOpen.set(false); this.flash('New pantry item added to the refill queue.'); await this.refreshAll();
    });
  }

  async claimRefill(refill: Refill): Promise<void> {
    const row = this.refillQueue().find(x => x.refill.id === refill.id);
    if (refill.status !== 'Assigned' || !row?.canAcceptOffer) {
      this.error.set('This refill has not been offered to you. It will be available here when it is your turn.');
      await this.refreshAll();
      return;
    }
    if (!row?.itemRowVersion) { this.error.set('This refill changed. Refresh the queue and try again.'); await this.refreshAll(); return; }
    await this.mutate(async () => {
      await this.post(`offices/${refill.office}/refill-requests/${refill.id}/claim`, { refillRowVersion: refill.rowVersion, itemRowVersion: row.itemRowVersion });
      this.workingOffice = refill.office;
      this.flash(`${refill.item} is yours to refill.`); await this.refreshAll();
    });
  }

  async acceptRefill(refill: Refill): Promise<void> {
    await this.mutate(async () => {
      const result = await this.post<{ refill: Refill; accepted: boolean; skipReason?: string }>(`refills/${refill.id}/accept`, {
        expectedVersion: refill.version, workingMode: this.workingMode, workingOffice: this.workingOffice
      });
      if (result.accepted) {
        this.flash('You’re at the office and ready to refill this item.');
      } else {
        this.flash(result.skipReason || 'This refill is back in the shared queue.');
      }
      await this.refreshAll();
    });
  }

  async passRefill(refill: Refill): Promise<void> {
    const reason = window.prompt('Why are you passing this refill offer?')?.trim();
    if (!reason) return;
    await this.mutate(async () => {
      await this.post(`refills/${refill.id}/deny`, { expectedVersion: refill.version, reason });
      this.flash('You passed on the offer. It will go to the next eligible employee.');
      await this.refreshAll();
    });
  }

  async completeRefill(refill: Refill): Promise<void> {
    if (!this.canCompleteRefill(refill)) { this.error.set('Confirm your work location before recording this refill.'); return; }
    if (this.refillQuantity <= 0 || this.refillAmount == null || this.refillAmount <= 0 || !this.refillDescription.trim()) { this.error.set('Enter the quantity, purchase total, and a short purchase note.'); return; }
    await this.mutate(async () => {
      await this.post(`refills/${refill.id}/complete`, { expectedVersion: refill.version, quantity: Number(this.refillQuantity), amount: Number(this.refillAmount), description: this.refillDescription.trim(), receiptImageBase64: null });
      this.flash(`${refill.item} was restocked.`); this.refillDescription = ''; this.refillAmount = null; await this.refreshAll();
    });
  }

  async submitClaim(): Promise<void> {
    const purchase = this.selectedPurchase();
    if (!purchase) { this.error.set('Choose one of your purchases before filing a reimbursement claim.'); return; }
    const currentStatus = this.purchaseClaimStatus(purchase);
    if (currentStatus && currentStatus !== 'Denied') { this.error.set('This purchase already has a claim in progress.'); return; }
    if (purchase.amount <= 0 || !this.claimDescription.trim()) { this.error.set('Add a short note about this purchase.'); return; }
    await this.mutate(async () => {
      const receipt = this.reimbursementReceipt();
      if (receipt && receipt.size > 5 * 1024 * 1024) throw new Error('Receipt must be 5 MB or smaller.');
      const receiptBase64 = receipt ? await this.fileBase64(receipt) : null;
      const resubmission = currentStatus === 'Denied';
      await this.post('me/claims', { purchaseId: purchase.purchaseId, idempotencyKey: purchase.purchaseId, office: purchase.office, amount: purchase.amount, currency: purchase.currency, description: this.claimDescription.trim(), receiptBase64, receiptMediaType: receipt?.type || null });
      this.clearClaimForm(); this.flash(resubmission ? 'Claim resubmitted to HR.' : 'Claim submitted to HR.'); await this.refreshAll();
    });
  }

  startPurchaseClaim(purchase: Purchase): void {
    const currentStatus = this.purchaseClaimStatus(purchase);
    if (currentStatus && currentStatus !== 'Denied') return;
    this.claimPurchaseId.set(purchase.purchaseId);
    this.claimDescription = purchase.description;
    this.reimbursementReceipt.set(null);
    this.page.set('claims'); this.error.set('');
  }

  clearClaimForm(): void {
    this.claimPurchaseId.set(''); this.claimDescription = ''; this.reimbursementReceipt.set(null);
  }

  purchaseClaimLabel(purchase: Purchase): string {
    const entry = this.claimHistory().find(item => item.purchaseKey === purchase.purchaseId);
    if (entry) return this.claimHistoryStatus(entry);
    const status = this.purchaseClaimStatus(purchase);
    if (!status) return 'Not claimed';
    if (status === 'Submitted' && (purchase.submissionAttempt ?? 1) > 1) return 'Resubmitted';
    return this.statusLabel(status);
  }

  purchaseClaimStatus(purchase: Purchase): string | undefined {
    return this.claimHistory().find(item => item.purchaseKey === purchase.purchaseId)?.claim.status ?? purchase.claimStatus;
  }

  claimHistoryStatus(entry: ClaimHistoryEntry): string {
    if (entry.attempts > 1 && ['Submitted', 'Processing', 'Approved'].includes(entry.claim.status)) return 'Resubmitted';
    return this.statusLabel(entry.claim.status);
  }

  openChatClaimForm(): void {
    this.navigate('claims');
  }

  selectReceipt(event: Event): void {
    const input = event.target as HTMLInputElement;
    const file = input.files?.[0] ?? null;
    if (file && file.size > 5 * 1024 * 1024) { this.error.set('Receipt must be 5 MB or smaller.'); input.value = ''; this.reimbursementReceipt.set(null); return; }
    this.reimbursementReceipt.set(file);
  }

  async createLeave(): Promise<void> {
    if (!this.leaveStart || !this.leaveEnd) { this.error.set('Choose the first and last day of leave.'); return; }
    if (this.leaveEnd < this.leaveStart) { this.error.set('The leave end date must be on or after its start date.'); return; }
    if (this.leaveDateConflict()) { this.error.set('These dates overlap planned leave or an office visit. Choose different dates.'); return; }
    await this.mutate(async () => {
      await this.post('me/leaves', { office: this.selectedOffice(), startDate: this.leaveStart, endDate: this.leaveEnd });
      this.flash('Planned leave saved.'); this.leaveStart = ''; this.leaveEnd = ''; await this.refreshAll();
    });
  }

  async scheduleOfficeVisit(): Promise<void> {
    if (!this.visitStartDate || !this.visitEndDate) { this.error.set('Choose the first and last day of your office visit.'); return; }
    if (this.visitEndDate < this.visitStartDate) { this.error.set('The visit end date must be on or after its start date.'); return; }
    if (this.visitDateConflict()) { this.error.set('These dates overlap planned leave or another office visit. Choose different dates.'); return; }
    const span = (Date.parse(`${this.visitEndDate}T00:00:00Z`) - Date.parse(`${this.visitStartDate}T00:00:00Z`)) / 86_400_000;
    if (span >= 14) { this.error.set('Office visits can last up to 14 calendar days.'); return; }
    await this.mutate(async () => {
      await this.post('me/office-visits', { office: this.visitOffice, startDate: this.visitStartDate, endDate: this.visitEndDate });
      this.visitStartDate = ''; this.visitEndDate = '';
      this.flash('Office visit scheduled. Pantry access starts on its first day.');
      await this.refreshAll();
      await this.connectHub();
    });
  }

  async cancelOfficeVisit(visit: OfficeVisit): Promise<void> {
    await this.mutate(async () => {
      await this.request<void>('DELETE', `me/office-visits/${visit.id}`);
      this.flash('Office visit cancelled.');
      await this.refreshAll();
      await this.connectHub();
    });
  }

  officeVisitStatus(visit: OfficeVisit): string {
    if (visit.cancelledAt) return 'Cancelled';
    const today = this.officeLocalDate(visit.office);
    if (visit.endDate < today) return 'Completed';
    return visit.startDate > today ? 'Scheduled' : 'Active';
  }

  officeVisitDay(value: string): string {
    const date = this.parseOfficeVisitDate(value);
    return date ? new Intl.DateTimeFormat(undefined, { day: 'numeric', timeZone: 'UTC' }).format(date) : '–';
  }

  officeVisitDate(value: string): string {
    const date = this.parseOfficeVisitDate(value);
    return date ? new Intl.DateTimeFormat(undefined, { month: 'short', day: 'numeric', year: 'numeric', timeZone: 'UTC' }).format(date) : '–';
  }

  leaveDateConflict(): boolean {
    return !!this.leaveStart && !!this.leaveEnd && this.leaveEnd >= this.leaveStart &&
      (this.hasScheduledVisitOverlap(this.leaveStart, this.leaveEnd) || this.leaves().some(leave =>
        leave.status !== 'Cancelled' && this.dateRangesOverlap(this.leaveStart, this.leaveEnd, leave.startDate, leave.endDate)));
  }

  visitDateConflict(): boolean {
    return !!this.visitStartDate && !!this.visitEndDate && this.visitEndDate >= this.visitStartDate &&
      (this.hasScheduledVisitOverlap(this.visitStartDate, this.visitEndDate) || this.leaves().some(leave =>
        leave.status !== 'Cancelled' && this.dateRangesOverlap(this.visitStartDate, this.visitEndDate, leave.startDate, leave.endDate)));
  }

  private hasScheduledVisitOverlap(startDate: string, endDate: string): boolean {
    return this.officeVisits().some(visit => !visit.cancelledAt &&
      this.dateRangesOverlap(startDate, endDate, visit.startDate, visit.endDate));
  }

  private dateRangesOverlap(startA: string, endA: string, startB: string, endB: string): boolean {
    return startA <= endB && endA >= startB;
  }

  private parseOfficeVisitDate(value: string): Date | undefined {
    const match = /^(\d{4})-(\d{2})-(\d{2})$/.exec(value);
    if (!match) return undefined;
    const year = Number(match[1]);
    const month = Number(match[2]);
    const day = Number(match[3]);
    const date = new Date(Date.UTC(year, month - 1, day));
    return date.getUTCFullYear() === year && date.getUTCMonth() === month - 1 && date.getUTCDate() === day ? date : undefined;
  }

  officeLocalDate(office: Office): string {
    const parts = new Intl.DateTimeFormat('en-CA', { timeZone: this.officeTimeZones()[office], year: 'numeric', month: '2-digit', day: '2-digit' })
      .formatToParts(new Date());
    const part = (type: string) => parts.find(value => value.type === type)?.value ?? '';
    return `${part('year')}-${part('month')}-${part('day')}`;
  }

  private officeForToday(visits: OfficeVisit[], user: User): Office {
    const visit = visits.find(candidate => !candidate.cancelledAt &&
      candidate.startDate <= this.officeLocalDate(candidate.office) &&
      candidate.endDate >= this.officeLocalDate(candidate.office));
    return visit?.office ?? user.homeOffice;
  }

  @HostListener('document:keydown.escape')
  closeMobileNavigation(): void {
    if (this.mobileNavOpen()) {
      this.mobileNavOpen.set(false);
      window.setTimeout(() => document.querySelector<HTMLButtonElement>('.mobile-menu')?.focus(), 0);
    }
  }

  async cancelLeave(leave: Leave): Promise<void> {
    await this.mutate(async () => { await this.request<void>('DELETE', `me/leaves/${leave.id}`); this.flash('Planned leave cancelled.'); await this.refreshAll(); });
  }

  async takeClaim(claim: Claim): Promise<void> {
    await this.mutate(async () => { await this.post(`hr/claims/${claim.id}/claim`, { rowVersion: claim.rowVersion }); this.flash('Claim assigned to you for review.'); await this.refreshAll(); });
  }

  async decideClaim(claim: Claim, approve: boolean): Promise<void> {
    const reason = approve ? '' : window.prompt('Reason for declining this claim:')?.trim();
    if (!approve && !reason) return;
    await this.mutate(async () => {
      const result = await this.post<Claim | { claimId: string; status: string; awaitingWebhook: boolean }>(
        `hr/claims/${claim.id}/decision`, { rowVersion: claim.rowVersion, approve, reason });
      if ('awaitingWebhook' in result && result.awaitingWebhook) {
        this.pendingExternalClaimDecisions.update(current => new Set(current).add(claim.id));
        this.flash('Decision sent to Mock HR. Waiting for confirmation.');
      } else if ('id' in result) {
        this.claims.update(items => items.map(item => item.id === result.id ? result : item));
        this.flash(approve ? 'Claim approved.' : 'Claim declined.');
      }
      await this.refreshAll();
    });
  }

  async reimburseClaim(claim: Claim): Promise<void> {
    const payrollCurrency = claim.employeePayrollCurrency ?? claim.currency;
    const needsConversion = payrollCurrency !== claim.currency;
    const exchangeRate = needsConversion ? this.payrollRates[claim.id] : null;
    const exchangeRateSource = needsConversion ? this.payrollRateSources[claim.id]?.trim() : null;
    if (needsConversion && (exchangeRate == null || !Number.isFinite(exchangeRate) || exchangeRate <= 0 || exchangeRate > 99_999_999_999 || !exchangeRateSource || exchangeRateSource.length > 200)) {
      this.error.set('Enter a positive exchange rate and its source for the employee payroll currency.');
      return;
    }
    const payrollAmount = needsConversion ? Math.round(claim.amount * exchangeRate! * 100) / 100 : claim.amount;
    if (!Number.isFinite(payrollAmount) || payrollAmount <= 0) {
      this.error.set('The converted payroll amount is outside the supported range.');
      return;
    }
    await this.mutate(async () => {
      await this.post(`hr/claims/${claim.id}/reimburse`, {
        rowVersion: claim.rowVersion, payrollAmount, payrollCurrency, exchangeRate,
        exchangeRateAt: payrollCurrency === claim.currency ? null : new Date().toISOString(),
        exchangeRateSource,
      });
      this.flash('Claim marked as reimbursed.'); await this.refreshAll();
    });
  }

  async openReceipt(claim: Claim): Promise<void> {
    try {
      const blob = await firstValueFrom(this.http.get(`${API}/hr/claims/${claim.id}/receipt`, { headers: this.headers(), responseType: 'blob' }));
      const url = URL.createObjectURL(blob);
      window.open(url, '_blank', 'noopener');
      window.setTimeout(() => URL.revokeObjectURL(url), 60_000);
    } catch (error) { this.setError(error, 'The receipt could not be opened.'); }
  }

  async openConversation(conversation: Conversation): Promise<void> {
    this.activeConversation.set(conversation); this.messages.set([]);
    const requestId = ++this.messageLoadGeneration;
    try {
      const page = await this.get<{ items: Message[] }>(`conversations/${conversation.id}/messages`);
      if (requestId === this.messageLoadGeneration && this.activeConversation()?.id === conversation.id) this.messages.set(page.items);
    }
    catch (error) {
      if (requestId === this.messageLoadGeneration && this.activeConversation()?.id === conversation.id)
        this.setError(error, 'Messages could not be loaded.');
    }
  }

  async sendMessage(): Promise<void> {
    const conversation = this.activeConversation(); if (!conversation || !this.chatText.trim()) return;
    const text = this.chatText.trim();
    await this.mutate(async () => {
      const message = await this.post<Message>(`conversations/${conversation.id}/messages`, { text });
      if (this.activeConversation()?.id === conversation.id) this.messages.update(list => [...list, message]);
      if (this.chatText.trim() === text) this.chatText = '';
    });
  }

  async loadReports(): Promise<void> {
    await this.mutate(async () => {
      const data = await this.get<ReportSnapshot>(`reports/snapshot?month=${this.month}`);
      this.reports.set(data.offices); this.reportTotals.set(data.totalsByCurrency);
    });
  }

  async logout(): Promise<void> {
    if (this.officeAccessTimer) window.clearInterval(this.officeAccessTimer);
    this.officeAccessTimer = undefined;
    await this.hub?.stop(); this.hub = undefined;
    this.refreshGeneration++; this.messageLoadGeneration++;
    localStorage.removeItem('snackcounter.email');
    this.user.set(null); this.clearAccountState(); this.loading.set(false); this.busy.set(false); this.page.set('overview');
  }

  navigate(page: string): void { this.page.set(page); this.mobileNavOpen.set(false); this.error.set(''); if (page === 'chat' && this.conversations().length && !this.activeConversation()) void this.openConversation(this.conversations()[0]); }
  isActive(page: string): boolean { return this.page() === page; }
  hasRefillRequest(item: Snack): boolean { return this.refillQueue().some(row => row.refill.snackItemId === item.id); }
  refillForItem(item: Snack): Refill | undefined { return this.refillQueue().find(row => row.refill.snackItemId === item.id)?.refill; }
  isAssignedToMe(refill: Refill): boolean { return !!refill.assignedEmployeeId && refill.assignedEmployeeId.toLocaleLowerCase() === this.user()?.employeeId.toLocaleLowerCase(); }
  refillClaimLabel(refill?: Refill): string {
    if (!refill?.assignedEmployeeId) return 'Waiting for the next offer';
    if (this.isAssignedToMe(refill)) return 'Offered to you';
    const claimant = this.refillQueue().find(entry => entry.refill.id === refill.id)?.suggestedEmployeeName;
    return claimant ? `Offered to ${claimant}` : 'Offered to another employee';
  }
  refillOfferState(refillId: string): RefillOfferState {
    return this.refillQueue().find(entry => entry.refill.id === refillId)?.offerState ?? 'None';
  }
  canCompleteRefill(refill: Refill): boolean {
    if (refill.status !== 'Assigned' || !this.isAssignedToMe(refill)) return false;
    const expiresAt = refill.assignmentExpiresAt ? new Date(refill.assignmentExpiresAt).getTime() : 0;
    if (expiresAt <= Date.now()) return false;
    return this.refillOfferState(refill.id) === 'Accepted';
  }
  refillQueueLabel(entry: OfficeRefill): string {
    if (entry.offerState === 'Acknowledged' && this.isAssignedToMe(entry.refill)) return 'Reserved for you';
    return this.refillClaimLabel(entry.refill);
  }
  formatMoney(amount: number | undefined, currency: Currency | undefined): string { const unit = currency ?? this.selectedCurrency(); return new Intl.NumberFormat(unit === 'NOK' ? 'en-NO' : 'en-IN', { style: 'currency', currency: unit, maximumFractionDigits: 2 }).format(amount ?? 0); }
  monthlyBudgetUsePercent(): number { const overview = this.monthlyOverview(); return overview?.budget ? Math.min(100, overview.spent / overview.budget * 100) : 0; }
  shortDate(value?: string): string { return value ? new Date(value).toLocaleDateString(undefined, { day: 'numeric', month: 'short' }) : '–'; }
  statusLabel(value: string): string { return value.replace(/([a-z])([A-Z])/g, '$1 $2'); }
  initials(name?: string): string { return (name ?? 'SC').split(' ').map(x => x[0]).join('').slice(0, 2).toUpperCase(); }
  isMyClaim(claim: Claim): boolean { return claim.employeeId === this.user()?.employeeId; }
  isCurrentProcessor(claim: Claim): boolean { return claim.processingByEmployeeId === this.user()?.employeeId; }
  isAwaitingExternalClaimDecision(claim: Claim): boolean { return this.pendingExternalClaimDecisions().has(claim.id); }
  async markRead(notification: Notification): Promise<void> {
    const employeeId = this.user()?.employeeId;
    if (!notification.readAt && employeeId) {
      await this.request<void>('POST', `me/notifications/${notification.id}/read`, {});
      if (this.user()?.employeeId === employeeId)
        this.notifications.update(list => list.map(x => x.id === notification.id ? { ...x, readAt: new Date().toISOString() } : x));
    }
  }

  notificationTitle(notification: Notification): string {
    const titles: Record<string, string> = {
      RefillOffered: 'A refill has your name on it',
      RefillClaimed: 'A refill offer was acknowledged',
      RefillNeeded: 'An item needs refilling',
      RefillAvailable: 'A refill is back in the queue',
      RefillOfferPassed: 'A refill offer moved to the next person',
      RefillOfferExpired: 'Your refill offer expired',
      RefillAssignmentExpired: 'Your refill task timed out',
      RefillReoffered: 'A refill is being offered again',
      RefillCompleted: 'A shelf has been restocked',
      EmptyItemReported: 'Empty item reported',
      StockDepleted: 'A pantry item has run out',
      MonthlyReportReady: 'Monthly pantry report is ready',
      MonthlyConsumptionRanked: 'This month’s most used items',
      BudgetThresholdReached: 'Pantry budget is getting close',
      BudgetExceeded: 'Pantry budget was exceeded',
      BudgetMet: 'Pantry budget was fully used',
      BudgetMetAtClose: 'Pantry budget was fully used',
      BudgetUnusedAtClose: 'Pantry budget closed with funds left',
      ClaimSubmitted: 'A reimbursement is waiting for review',
      ClaimDecided: 'There’s an update on your reimbursement',
      ClaimReimbursed: 'Your reimbursement has been paid',
      HRChatMessage: 'A new message in the HR chat',
      OfficeChatMessage: 'A new message in the office chat',
    };
    return titles[notification.eventType] ?? this.statusLabel(notification.eventType);
  }

  notificationMark(notification: Notification): string {
    const event = notification.eventType;
    if (event.includes('Refill')) return 'R';
    if (event.includes('Claim')) return 'C';
    if (event.includes('Report') || event.includes('Budget') || event === 'MonthlyConsumptionRanked') return 'B';
    if (event.includes('ChatMessage')) return 'M';
    return 'S';
  }

  notificationSummary(notification: Notification): string {
    const payload = this.notificationPayload(notification);
    const office = this.notificationOffice(payload.office ?? notification.office);
    const month = this.notificationMonth(payload.month);
    const currency = notification.eventType === 'ClaimReimbursed'
      ? this.notificationCurrency(payload.payrollCurrency) || this.notificationCurrency(payload.currency) || this.officeCurrency(office)
      : this.notificationCurrency(payload.currency) || this.officeCurrency(office);
    const money = (amount: unknown) => typeof amount === 'number' ? this.formatEventMoney(amount, currency) : '';
    switch (notification.eventType) {
      case 'RefillCompleted': {
        const item = this.inventory().find(candidate => candidate.id === payload.itemId)?.name;
        const quantity = typeof payload.quantity === 'number' ? `${payload.quantity} added` : '';
        const total = money(payload.amount);
        return [item, quantity, total && `${total} spent`, office].filter(Boolean).join(' · ') || 'The pantry inventory has been updated.';
      }
      case 'RefillOffered':
        return [office, payload.expiresAt && `Please respond by ${new Date(payload.expiresAt).toLocaleString()}`].filter(Boolean).join(' · ') || 'Open refill work to respond to your offer.';
      case 'RefillClaimed':
        return [office, payload.itemName].filter(Boolean).join(' · ') || 'A teammate acknowledged a refill offer.';
      case 'RefillNeeded':
      case 'RefillAvailable':
      case 'RefillOfferPassed':
      case 'RefillOfferExpired':
      case 'RefillAssignmentExpired':
      case 'RefillReoffered':
        return [office, payload.itemName].filter(Boolean).join(' · ') || 'Open the refill queue for the latest status.';
      case 'EmptyItemReported':
        return [office, payload.itemName, typeof payload.budgetRemaining === 'number' && currency ? `${money(payload.budgetRemaining)} remaining` : ''].filter(Boolean).join(' · ') || 'Your empty-item report was sent to the pantry queue.';
      case 'MonthlyReportReady':
        if (Array.isArray(payload.offices)) return `${month ? `${month} · ` : ''}Combined office report with separate currency totals.`;
        return [month, office, money(payload.budget) && `${money(payload.budget)} budget`, money(payload.spent) && `${money(payload.spent)} spent`, money(payload.remaining) && `${money(payload.remaining)} remaining`].filter(Boolean).join(' · ') || 'Open monthly reports for the office breakdown.';
      case 'MonthlyConsumptionRanked': {
        const items = Array.isArray(payload.topItems)
          ? [...payload.topItems].sort((a: { quantity?: number }, b: { quantity?: number }) => Math.abs(b.quantity ?? 0) - Math.abs(a.quantity ?? 0)).map((item: { item?: string }) => item.item).filter(Boolean).slice(0, 3).join(', ')
          : '';
        return [month, office, items && `Most used: ${items}`].filter(Boolean).join(' · ') || 'Open monthly reports to see the pantry rankings.';
      }
      case 'BudgetThresholdReached':
      case 'BudgetExceeded':
      case 'BudgetMet':
      case 'BudgetUnusedAtClose':
        return [month && `${month} close`, office, money(payload.spent) && `${money(payload.spent)} spent`, money(payload.remaining) && `${money(payload.remaining)} remaining`].filter(Boolean).join(' · ') || 'Open monthly reports for the budget details.';
      case 'ClaimSubmitted':
        return [office, money(payload.amount)].filter(Boolean).join(' · ') || 'Open the HR claim desk to review this request.';
      case 'ClaimDecided':
      case 'ClaimReimbursed':
        return [typeof payload.status === 'number' ? ['Submitted', 'Processing', 'Approved', 'Denied', 'Reimbursed'][payload.status] : payload.status, money(payload.payrollAmount ?? payload.amount), payload.reason].filter(Boolean).join(' · ') || 'Open reimbursements to see the latest status.';
      case 'StockDepleted':
        return [office, payload.itemName].filter(Boolean).join(' · ') || 'Check inventory and request a refill if needed.';
      case 'HRChatMessage':
      case 'OfficeChatMessage':
        return office ? `${office} · Open the conversation to read the message.` : 'Open chat to read the new message.';
      default:
        return 'Open Snack Counter to see the latest activity.';
    }
  }

  async openNotification(notification: Notification): Promise<void> {
    try { await this.markRead(notification); } catch (error) { this.setError(error, 'This update could not be marked as read.'); }
    const event = notification.eventType;
    const payload = this.notificationPayload(notification);
    if (event === 'PlannedLeavePrompt') this.navigate('leave');
    else if (event.startsWith('Refill') || event === 'StockDepleted' || event === 'EmptyItemReported') {
      this.navigate('requests');
    }
    else if (event.includes('Claim')) this.navigate(this.isHr() ? 'hr' : 'claims');
    else if (event.includes('Report') || event.includes('Budget') || event === 'MonthlyConsumptionRanked') {
      if (this.isHr() || this.isManager()) this.navigate('reports');
      else {
        this.navigate('requests');
      }
    }
    else if (event.includes('ChatMessage')) {
      this.navigate('chat');
      const target = notification.isHrAudience
        ? this.conversations().find(conversation => conversation.kind === 'Hr')
        : this.conversations().find(conversation => conversation.kind === 'Office' && conversation.office === notification.office);
      if (target) await this.openConversation(target);
    }
  }

  private async connectHub(): Promise<void> {
    await this.hub?.stop();
    this.hub = new HubConnectionBuilder().withUrl(HUB, { accessTokenFactory: () => this.user()?.email ?? '' }).withAutomaticReconnect().configureLogging(LogLevel.Warning).build();
    this.hub.on('officeQueueChanged', (change: { office: Office }) => {
      if (change.office === this.selectedOffice()) void this.reloadOfficePantry();
    });
    this.hub.onreconnected(() => {
      void this.reloadOfficePantry();
      void this.reloadHrClaims();
    });
    this.hub.on('claimStateChanged', () => void this.reloadHrClaims());
    this.hub.on('notification', (notification: Notification) => {
      const isPersonal = notification.recipientEmployeeId === this.user()?.employeeId;
      const isHrInbox = notification.isHrAudience === true && this.isHr();
      if ((!notification.office || notification.office === this.selectedOffice() || isPersonal || isHrInbox) && !this.notifications().some(x => x.id === notification.id)) this.notifications.update(items => [notification, ...items]);
      if (isPersonal && notification.eventType.startsWith('Claim')) {
        void this.get<Claim[]>('me/claims').then(items => this.employeeClaims.set(items)).catch(() => undefined);
        void this.get<Purchase[]>('me/purchases').then(items => this.purchases.set(items)).catch(() => undefined);
      }
      if (isHrInbox) void this.reloadHrClaims();
      const refillEvents = ['RefillNeeded', 'RefillClaimed', 'RefillAvailable', 'RefillOffered', 'RefillOfferPassed', 'RefillOfferExpired', 'RefillAssignmentExpired', 'RefillReoffered', 'RefillCompleted', 'EmptyItemReported', 'StockDepleted', 'BudgetThresholdReached', 'BudgetMet', 'BudgetMetAtClose', 'BudgetExceeded', 'BudgetUnusedAtClose'];
      const eventOffice = this.notificationOffice(notification.office ?? this.notificationPayload(notification).office);
      if (eventOffice === this.selectedOffice() && refillEvents.includes(notification.eventType)) {
        void this.reloadOfficePantry();
      }
      if (this.page() === 'chat') void this.reloadActiveMessages();
    });
    try { await this.hub.start(); } catch { /* The inbox remains available from the updates page. */ }
  }
  private async reloadOfficePantry(): Promise<void> {
    const office = this.selectedOffice();
    const employeeId = this.user()?.employeeId;
    if (!employeeId) return;
    try {
      const [inventory, queue, overview, refills] = await Promise.all([
        this.get<Snack[]>(`offices/${office}/inventory`),
        this.get<OfficeRefill[]>(`offices/${office}/refill-requests`),
        this.get<PantryOverview>(`offices/${office}/monthly-overview`),
        this.get<Refill[]>(`me/refills?office=${encodeURIComponent(office)}`),
      ]);
      if (office === this.selectedOffice() && employeeId === this.user()?.employeeId) {
        this.inventory.set(inventory); this.refillQueue.set(queue); this.monthlyOverview.set(overview); this.refills.set(refills);
      }
    } catch { /* Keep the last pantry view; manual refresh remains available. */ }
  }
  private async reloadActiveMessages(): Promise<void> {
    const conversation = this.activeConversation(); if (!conversation) return;
    const requestId = ++this.messageLoadGeneration;
    try {
      const page = await this.get<{ items: Message[] }>(`conversations/${conversation.id}/messages`);
      if (requestId === this.messageLoadGeneration && this.activeConversation()?.id === conversation.id) this.messages.set(page.items);
    } catch { /* Keep the last visible history until connectivity returns. */ }
  }
  private async reloadHrClaims(): Promise<void> {
    const employeeId = this.user()?.employeeId;
    if (!employeeId || !this.isHr()) return;
    try {
      const items = await this.get<Claim[]>('hr/claims');
      if (this.user()?.employeeId === employeeId) this.setHrClaims(items);
    } catch { /* Keep the current queue until the next refresh or SignalR update. */ }
  }
  private setHrClaims(items: Claim[]): void {
    this.claims.set(items);
    this.pendingExternalClaimDecisions.update(pending => new Set(
      [...pending].filter(id => items.some(claim => claim.id === id && claim.status === 'Processing'))
    ));
  }
  private applyConversations(conversations: Conversation[]): void {
    this.conversations.set(conversations);
    const activeId = this.activeConversation()?.id;
    if (activeId) {
      const updated = conversations.find(conversation => conversation.id === activeId);
      if (updated) this.activeConversation.set(updated);
      else { this.activeConversation.set(null); this.messages.set([]); this.messageLoadGeneration++; }
    }
  }
  private clearAccountState(): void {
    this.refreshGeneration++; this.messageLoadGeneration++;
    this.inventory.set([]); this.refills.set([]); this.refillQueue.set([]); this.claims.set([]);
    this.pendingExternalClaimDecisions.set(new Set());
    this.employeeClaims.set([]); this.purchases.set([]); this.leaves.set([]); this.reports.set([]);
    this.reportTotals.set({}); this.monthlyOverview.set(null); this.notifications.set([]);
    this.conversations.set([]); this.activeConversation.set(null); this.messages.set([]);
    this.officeVisits.set([]);
    this.claimPurchaseId.set(''); this.claimDescription = '';
    this.reimbursementReceipt.set(null); this.leaveStart = ''; this.leaveEnd = ''; this.chatText = '';
    this.payrollRates = {}; this.payrollRateSources = {};
    this.visitStartDate = ''; this.visitEndDate = '';
    this.refillQuantity = 1; this.refillAmount = null; this.refillDescription = '';
    this.emptyReportItemId = ''; this.newItemName = ''; this.newPantryItemName = '';
    this.reportEmptyOpen.set(false); this.newItemFormOpen.set(false); this.error.set(''); this.notice.set('');
  }
  private async get<T>(path: string): Promise<T> { return firstValueFrom(this.http.get<T>(`${API}/${path}`, { headers: this.headers() })); }
  private async post<T = unknown>(path: string, body: unknown): Promise<T> { return firstValueFrom(this.http.post<T>(`${API}/${path}`, body, { headers: this.headers() })); }
  private async request<T>(method: string, path: string, body?: unknown): Promise<T> { return firstValueFrom(this.http.request<T>(method, `${API}/${path}`, { body, headers: this.headers() })); }
  private headers(): Record<string, string> { return this.user() ? { 'X-Demo-Email': this.user()!.email } : {}; }
  private async fileBase64(file: File): Promise<string> {
    const dataUrl = await new Promise<string>((resolve, reject) => {
      const reader = new FileReader(); reader.onerror = () => reject(new Error('Receipt could not be read.'));
      reader.onload = () => resolve(String(reader.result)); reader.readAsDataURL(file);
    });
    return dataUrl.slice(dataUrl.indexOf(',') + 1);
  }
  private notificationPayload(notification: Notification): any {
    try { const value = notification.payloadJson ? JSON.parse(notification.payloadJson) : {}; return value && typeof value === 'object' ? value : {}; }
    catch { return {}; }
  }
  private notificationOffice(value: unknown): string {
    if (value === 'Oslo' || value === 0) return 'Oslo';
    if (value === 'Bengaluru' || value === 1) return 'Bengaluru';
    return '';
  }
  private notificationCurrency(value: unknown): Currency | '' {
    if (value === 'NOK' || value === 0) return 'NOK';
    if (value === 'INR' || value === 1) return 'INR';
    return '';
  }
  private officeCurrency(office: string): Currency | '' {
    return office === 'Oslo' || office === 'Bengaluru' ? this.officeCurrencies()[office] : '';
  }
  private notificationMonth(value: unknown): string {
    if (typeof value !== 'string' || !/^\d{4}-\d{2}$/.test(value)) return '';
    return new Date(`${value}-01T12:00:00`).toLocaleDateString(undefined, { month: 'long', year: 'numeric' });
  }
  private formatEventMoney(amount: number, currency: Currency | ''): string {
    if (!currency) return 'Currency unavailable';
    const value = new Intl.NumberFormat(currency === 'NOK' ? 'en-NO' : 'en-IN', { maximumFractionDigits: 2 }).format(amount);
    return `${currency} ${value}`;
  }
  private async mutate(action: () => Promise<void>): Promise<void> {
    this.busy.set(true); this.error.set('');
    try { await action(); }
    catch (error) {
      if (error instanceof HttpErrorResponse && error.status === 409) await this.refreshAll();
      this.setError(error, 'That action could not be completed.');
    } finally { this.busy.set(false); }
  }
  private setError(error: unknown, fallback: string): void {
    if (!(error instanceof HttpErrorResponse)) {
      this.error.set(error instanceof Error && error.message ? error.message : fallback);
      return;
    }

    const problem = this.problemMessage(error);
    if (problem) {
      this.error.set(problem);
      return;
    }

    const friendlyByStatus: Record<number, string> = {
      0: 'Snack Counter could not reach the server. Check that the API is running, then try again.',
      400: 'Some of the information needs attention. Review the form and try again.',
      401: 'Your session has expired. Sign in again to continue.',
      403: 'You do not have access to do that.',
      404: 'That item or request is no longer available. Refresh the page and try again.',
      409: 'The request conflicts with the current state. Refresh and try again.',
      413: 'That file is too large. Choose a file under 5 MB and try again.',
      429: 'Too many requests were sent at once. Wait a moment and try again.'
    };
    this.error.set(friendlyByStatus[error.status] ?? (error.status >= 500
      ? 'The server could not complete that request. Try again in a moment.'
      : fallback));
  }

  private problemMessage(error: HttpErrorResponse): string | undefined {
    const readMessage = (body: unknown, depth = 0): string | undefined => {
      if (depth > 3) return undefined;
      if (Array.isArray(body)) {
        for (const value of body) {
          const message = readMessage(value, depth + 1);
          if (message) return message;
        }
        return undefined;
      }
      if (typeof body === 'string') {
        const text = body.trim();
        if (!text) return undefined;
        try { return readMessage(JSON.parse(text), depth + 1); }
        catch { return text.length <= 300 && !/<[^>]*>/.test(text) ? text : undefined; }
      }
      if (!body || typeof body !== 'object') return undefined;

      const problem = body as Record<string, unknown>;
      const get = (key: string) => problem[key] ?? problem[Object.keys(problem).find(name => name.toLocaleLowerCase() === key) ?? ''];
      for (const value of [get('detail'), get('message')]) {
        const message = readMessage(value, depth + 1);
        if (message) return message;
      }
      const nestedError = get('error');
      if (nestedError && typeof nestedError === 'object') {
        const message = readMessage(nestedError, depth + 1);
        if (message) return message;
      }
      const validationErrors = get('errors');
      if (validationErrors && typeof validationErrors === 'object') {
        for (const value of Object.values(validationErrors)) {
          const message = readMessage(value, depth + 1);
          if (message) return message;
        }
      }
      const title = get('title');
      if (title !== undefined) {
        const message = readMessage(title, depth + 1);
        if (message) return message;
      }
      return undefined;
    };

    return readMessage(error.error);
  }
  private flash(message: string): void { this.notice.set(message); window.setTimeout(() => { if (this.notice() === message) this.notice.set(''); }, 4200); }
}
